using Avalonia.Threading;

namespace Josour.App.Services;

/// <summary>
/// Posting work to the UI thread, in one place so that no view model has to know which toolkit is underneath.
/// <para>
/// Every control-channel frame arrives on a socket thread, so this is on the path of nearly everything the user sees.
/// </para>
/// </summary>
public static class UiThread
{
    /// <summary>Runs the action on the UI thread; immediately when already on it.</summary>
    public static void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }

    /// <summary>Runs the function on the UI thread and awaits its result.</summary>
    public static Task<T> InvokeAsync<T>(Func<T> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return Dispatcher.UIThread.CheckAccess()
            ? Task.FromResult(function())
            : Dispatcher.UIThread.InvokeAsync(function).GetTask();
    }

    /// <summary>Runs the action on the UI thread and awaits it.</summary>
    public static Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }
}
