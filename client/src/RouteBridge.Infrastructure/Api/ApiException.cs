using System.Net;

namespace RouteBridge.Infrastructure.Api;

/// <summary>Error codes of docs/api.md plus the client-side ones this assembly produces.</summary>
public static class ApiErrorCodes
{
    public const string InvalidCredentials = "invalid_credentials";
    public const string AccountLocked = "account_locked";
    public const string AccountDisabled = "account_disabled";
    public const string DeviceRevoked = "device_revoked";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string ValidationError = "validation_error";
    public const string RateLimited = "rate_limited";
    public const string Conflict = "conflict";
    public const string Internal = "internal";

    /// <summary>Client-side: the server answered 2xx but the body was not the expected JSON.</summary>
    public const string InvalidResponse = "invalid_response";

    /// <summary>Client-side: a non-2xx status without the <c>{"error":{…}}</c> envelope (proxy, captive portal, crash page).</summary>
    public const string HttpError = "http_error";
}

/// <summary>The server answered with an error envelope <c>{"error":{"code","message"}}</c> (or a non-2xx status without one).</summary>
public sealed class ApiException : Exception
{
    public ApiException(HttpStatusCode statusCode, string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        Code = code ?? throw new ArgumentNullException(nameof(code));
    }

    public HttpStatusCode StatusCode { get; }

    /// <summary>Machine-readable code, e.g. <c>invalid_credentials</c>. See <see cref="ApiErrorCodes"/>.</summary>
    public string Code { get; }

    public override string ToString() => $"{nameof(ApiException)}: {(int)StatusCode} {Code}: {Message}";
}

/// <summary>The server could not be reached or did not answer in time (DNS, TCP, TLS, timeout). Not a server-side error.</summary>
public sealed class ApiUnavailableException : Exception
{
    public ApiUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
