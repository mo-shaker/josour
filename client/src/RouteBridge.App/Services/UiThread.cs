using System.Windows;

namespace RouteBridge.App.Services;

/// <summary>Runs work on the WPF dispatcher; when there is no application (unit tests, shutdown) the work runs inline.</summary>
internal static class UiThread
{
    public static void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            action();
        }
        else if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
