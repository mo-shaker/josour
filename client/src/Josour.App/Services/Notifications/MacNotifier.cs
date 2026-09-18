using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Josour.App.Services.Notifications;

/// <summary>
/// Notification Centre banners on macOS, through <c>osascript</c>'s <c>display notification</c>.
/// <para>
/// Not <c>UNUserNotificationCenter</c>, and the reason is worth stating: that API refuses to deliver from a process
/// that is not inside a signed <c>.app</c> bundle with its own bundle identifier, which is exactly what Josour is not
/// yet (ADR-0011 and the Gatekeeper question it leaves open). <c>osascript</c> works today, from a bare binary, and
/// costs one short-lived process per notification — a price only paid when there is something to say.
/// </para>
/// <para>
/// The banner has NO buttons. macOS only offers them to a bundled app with a registered category, so on this platform
/// the request window is the whole prompt, and <see cref="ShowIncomingRequestAsync"/> is attention, not a question.
/// That is why it never carries Accept or Reject: a notification that looked like it could answer, and could not,
/// would be worse than one that plainly cannot.
/// </para>
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacNotifier : INotifier
{
    private const string OsaScript = "/usr/bin/osascript";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    private readonly ILogger<MacNotifier> _logger;

    public MacNotifier(ILogger<MacNotifier> logger) => _logger = logger;

    public void RegisterActivation() =>
        _logger.LogInformation("macOS notifications are posted per launch; nothing to register");

    public Task ShowIncomingRequestAsync(string guestName, string deviceName, int durationMin, Guid requestId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Post(
            Strings.ToastIncomingRequestTitle,
            string.Format(UiFlow.Culture, Strings.ToastIncomingRequestBodyFormat, guestName, UiFlow.Ltr(deviceName), durationMin));
        return Task.CompletedTask;
    }

    public void ShowInfo(string title, string text) => Post(title, text);

    /// <summary>Nothing to clear: a banner dismisses itself, and the app has no handle on it.</summary>
    public void ClearIncomingRequest(Guid requestId)
    {
    }

    private void Post(string title, string text)
    {
        try
        {
            // The strings are AppleScript literals, so a quote or a backslash in a guest's display name would end the
            // literal and let the rest of the name run as script. Escaping is not cosmetic here: the name comes from
            // another user.
            var script = $"display notification \"{Escape(text)}\" with title \"{Escape(title)}\"";

            using var process = Process.Start(new ProcessStartInfo(OsaScript)
            {
                ArgumentList = { "-e", script },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                _logger.LogWarning("Could not start osascript to show a notification");
                return;
            }

            if (!process.WaitForExit((int)Budget.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It exited between the check and the kill.
                }

                _logger.LogWarning("osascript did not return within {Budget}; notification abandoned", Budget);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Notifications turned off, or osascript missing. The request window is the guaranteed path.
            _logger.LogWarning(ex, "Could not show a macOS notification");
        }
    }

    /// <summary>Escapes a value for an AppleScript double-quoted literal: backslash first, then the quote.</summary>
    internal static string Escape(string? value) =>
        (value ?? string.Empty).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
