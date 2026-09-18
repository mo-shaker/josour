using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Josour.App.Services;

/// <summary>Puts text on the system clipboard.</summary>
public interface IClipboardService
{
    /// <summary>True when the text was copied. False means there was no window to copy through, or the platform refused.</summary>
    Task<bool> SetTextAsync(string text);
}

/// <summary>
/// <see cref="IClipboardService"/> over Avalonia's clipboard.
/// <para>
/// Avalonia hangs the clipboard off a window rather than off the application, because that is how the underlying
/// platforms work — a clipboard write is attributed to a window. So this looks for a visible one, preferring whichever
/// is active, and fails politely when there is none: the app can be running entirely in the tray, and "copy" from a
/// window that is not there is a request that cannot be honoured rather than an error worth throwing.
/// </para>
/// </summary>
public sealed class ClipboardService : IClipboardService
{
    public async Task<bool> SetTextAsync(string text)
    {
        var clipboard = await UiThread.InvokeAsync(() => FindWindow()?.Clipboard).ConfigureAwait(false);
        if (clipboard is null)
        {
            return false;
        }

        await clipboard.SetTextAsync(text).ConfigureAwait(false);
        return true;
    }

    private static Window? FindWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        var windows = desktop.Windows;
        return windows.FirstOrDefault(w => w.IsActive && w.IsVisible)
               ?? windows.FirstOrDefault(w => w.IsVisible);
    }
}
