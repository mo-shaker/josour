using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using Josour.Infrastructure.Localization;
using Josour.Mobile;
using Josour.Mobile.Platform;

namespace Josour.Droid.Platform;

/// <summary>
/// The notification channels and the two kinds of notification besides the service's own. An incoming request opens the
/// consent screen; it has no Accept button, because accepting means answering for someone else's browsing and that should
/// not be one tap on a locked phone in anybody's hand.
/// </summary>
internal sealed class AndroidNotifier : IMobileNotifier
{
    public const string HostingChannel = "hosting";
    public const string RequestsChannel = "requests";
    public const string InfoChannel = "info";
    private readonly Context _context;
    private int _nextInfoId = 3000;

    public AndroidNotifier(Context context)
    {
        _context = context;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O && context.GetSystemService(Context.NotificationService) is NotificationManager manager)
        {
            manager.CreateNotificationChannel(new NotificationChannel(HostingChannel, Tr.Get(UiStringKeys.MobileServiceChannelName), NotificationImportance.Low));
            manager.CreateNotificationChannel(new NotificationChannel(RequestsChannel, Tr.Get(UiStringKeys.MobileRequestsChannelName), NotificationImportance.High));
            manager.CreateNotificationChannel(new NotificationChannel(InfoChannel, Tr.Get(UiStringKeys.AppName), NotificationImportance.Default));
        }
    }

    public void ShowIncomingRequest(Guid requestId, string title, string body)
    {
        var notification = new NotificationCompat.Builder(_context, RequestsChannel)
            .SetSmallIcon(Resource.Drawable.ic_josour)
            .SetContentTitle(title)
            .SetContentText(body)
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(body + "\n" + Tr.Get(UiStringKeys.MobileRequestTapToAnswer)))
            .SetPriority(NotificationCompat.PriorityHigh)
            .SetCategory(NotificationCompat.CategoryCall)
            .SetAutoCancel(true)
            .SetContentIntent(OpenApp(requestId.GetHashCode()))
            .Build()!;
        Notify(IdFor(requestId), notification);
    }

    public void CancelIncomingRequest(Guid requestId)
    {
        try { NotificationManagerCompat.From(_context).Cancel(IdFor(requestId)); }
        catch (Exception) { }
    }

    public void ShowInfo(string title, string body)
    {
        var notification = new NotificationCompat.Builder(_context, InfoChannel)
            .SetSmallIcon(Resource.Drawable.ic_josour)
            .SetContentTitle(title)
            .SetContentText(body)
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(body))
            .SetAutoCancel(true)
            .SetContentIntent(OpenApp(0))
            .Build()!;
        Notify(Interlocked.Increment(ref _nextInfoId), notification);
    }

    internal PendingIntent OpenApp(int requestCode) =>
        PendingIntent.GetActivity(_context, requestCode, MainActivity.OpenIntent(_context), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;

    private void Notify(int id, Notification notification)
    {
        try
        {
            NotificationManagerCompat.From(_context).Notify(id, notification);
        }
        catch (Java.Lang.SecurityException)
        {
            // No notification permission: the request is still on screen when the app is opened.
        }
    }

    private static int IdFor(Guid requestId) => 2000 + (requestId.GetHashCode() & 0x3FF);
}
