namespace RouteBridge.Infrastructure.Api;

/// <summary>Normalisation and validation of the server URL the user types into the sign-in window.</summary>
public static class ServerUrl
{
    /// <summary>
    /// Parses an absolute http(s) URL and returns it with a trailing slash so that <c>api/v1/…</c> can be appended with
    /// <see cref="Uri(Uri, string)"/> (a path prefix such as <c>https://host/rb</c> is preserved). Query and fragment are rejected.
    /// </summary>
    public static bool TryNormalize(string? input, out Uri baseUri)
    {
        baseUri = null!;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/') + "/";
        baseUri = new Uri(uri.GetLeftPart(UriPartial.Authority) + path, UriKind.Absolute);
        return true;
    }

    /// <summary>The app only talks to HTTPS servers; plain HTTP is tolerated for loopback (developer backend on localhost).</summary>
    public static bool IsAllowed(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        return baseUri.Scheme == Uri.UriSchemeHttps || (baseUri.Scheme == Uri.UriSchemeHttp && baseUri.IsLoopback);
    }
}
