using System.Globalization;
using Josour.Core.Browser;

namespace Josour.Browser;

/// <summary>The work browser's command line, literally as in plan 8.2 (Chrome and Edge alike). Deliberately without --proxy-bypass-list.</summary>
public static class BrowserCommandLine
{
    public static IReadOnlyList<string> Arguments(BrowserLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ProxyPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(options), "proxy port out of range");
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProfileDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProbeUrl);
        return new[]
        {
            "--user-data-dir=" + options.ProfileDirectory,
            "--proxy-server=http://127.0.0.1:" + options.ProxyPort.ToString(CultureInfo.InvariantCulture),
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-sync",
            "--disable-background-networking",
            "--disable-component-update",
            "--disable-quic",
            "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
            "--hide-crash-restore-bubble",
            "--new-window",
            options.ProbeUrl,
        };
    }

    /// <summary>The textual form for reports (quoting arguments that contain spaces).</summary>
    public static string Render(string exePath, IEnumerable<string> arguments)
        => string.Join(" ", new[] { Quote(exePath) }.Concat(arguments.Select(Quote)));

    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        var eq = arg.IndexOf('=');
        // --name="value with spaces", as it is written in the documentation
        if (arg.StartsWith("--", StringComparison.Ordinal) && eq > 0)
            return arg[..(eq + 1)] + "\"" + arg[(eq + 1)..].Replace("\"", "\\\"") + "\"";
        return "\"" + arg.Replace("\"", "\\\"") + "\"";
    }

    public static string DefaultProfileDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Josour", "BrowserProfile");
}
