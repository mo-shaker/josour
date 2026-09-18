using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Josour.App.Services;

/// <summary>Plays the system's "look at this" sound.</summary>
public interface IAttentionSound
{
    void Play();
}

/// <summary>
/// The system alert sound, per platform, for the one moment the app needs the host to look: an incoming request.
/// <para>
/// It is never the only signal — the request window is top-most and activated, and there is a notification besides —
/// so every failure here is swallowed. A machine with its sound off must still be able to accept a request.
/// </para>
/// </summary>
public sealed class AttentionSound : IAttentionSound
{
    private readonly ILogger<AttentionSound> _logger;

    public AttentionSound(ILogger<AttentionSound> logger) => _logger = logger;

    public void Play()
    {
        try
        {
#if WINDOWS
            if (OperatingSystem.IsWindows())
            {
                System.Media.SystemSounds.Exclamation.Play();
                return;
            }
#endif

            if (OperatingSystem.IsMacOS())
            {
                PlayMac();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not play the attention sound");
        }
    }

    /// <summary>
    /// <c>afplay</c> on the user's chosen alert sound. Not <c>osascript -e beep</c>, which blocks until the sound has
    /// finished and would hold up showing the window behind it.
    /// </summary>
    private static void PlayMac()
    {
        using var process = Process.Start(new ProcessStartInfo("/usr/bin/afplay")
        {
            ArgumentList = { "/System/Library/Sounds/Ping.aiff" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        // Deliberately not awaited and not killed: it is a two-second sound in a process of its own, and the app has
        // a request window to put on screen.
        process?.Dispose();
    }
}
