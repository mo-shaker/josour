namespace Josour.App.Services.Notifications;

/// <summary>
/// The vocabulary of a notification activation: what the button was, and which request it belonged to.
/// <para>
/// Shared rather than owned by the Windows notifier, because the two sides of the round trip live in different
/// assemblies' worth of platform: only Windows builds the toast that carries these arguments, but the handler and the
/// presenter that read them back are built everywhere.
/// </para>
/// </summary>
public static class NotificationActions
{
    public const string ActionKey = "action";
    public const string RequestIdKey = "request_id";
    public const string Accept = "accept";
    public const string Reject = "reject";
    public const string Open = "open";

    /// <summary>
    /// Parses the <c>key=value;key=value</c> string a Windows toast activation carries back.
    /// <para>
    /// Written here rather than taken from <c>ToastArguments.Parse</c> so that the handler reading it is not a
    /// Windows-only file: the parser is five lines, and a platform seam through the middle of activation handling
    /// would have cost more than it saved. Malformed input yields whatever pairs were well-formed — an activation is
    /// never worth throwing over, and an unrecognised action simply opens the window.
    /// </para>
    /// </summary>
    public static Dictionary<string, string> Parse(string? arguments)
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(arguments))
        {
            return parsed;
        }

        foreach (var pair in arguments.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            if (key.Length == 0)
            {
                continue;
            }

            parsed[Unescape(key)] = Unescape(value);
        }

        return parsed;
    }

    private static string Unescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            // A stray '%' is not a reason to lose the whole activation.
            return value;
        }
    }
}
