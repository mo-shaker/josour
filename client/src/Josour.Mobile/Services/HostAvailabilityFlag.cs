using Josour.Infrastructure;

namespace Josour.Mobile.Services;

/// <summary>
/// Whether the user left the phone available. Android may stop the process (a low-memory kill, an update) and start it again
/// for the foreground service; the phone then comes back as it was left rather than silently unavailable.
/// </summary>
public sealed class HostAvailabilityFlag
{
    private readonly string _path;

    public HostAvailabilityFlag(string? path = null) =>
        _path = path ?? Path.Combine(AppPaths.LocalAppDataRoot, "host-available");

    public bool Read()
    {
        try { return File.Exists(_path); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void Write(bool available)
    {
        try
        {
            if (available)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, "1");
            }
            else if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Remembering is a convenience; failing to remember must not fail the toggle.
        }
    }
}
