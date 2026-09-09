using System.Net;
using System.Text;

namespace Josour.Infrastructure.Tests.Support;

/// <summary>Scripted <see cref="HttpMessageHandler"/>: routes by method + absolute path, records every request (body + key headers).</summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body, string? Authorization, string? IfNoneMatch, string? UserAgent);

    private sealed class Route
    {
        public required HttpMethod Method { get; init; }
        public required string Path { get; init; }
        public required Func<HttpRequestMessage, int, Task<HttpResponseMessage>> Respond { get; init; }
        public int Calls;
    }

    private readonly List<Route> _routes = new();
    private readonly object _gate = new();

    public List<RecordedRequest> Requests { get; } = new();

    /// <summary>When set, every request throws this instead of being routed (simulates DNS/TCP failures).</summary>
    public Exception? Throw { get; set; }

    public void On(HttpMethod method, string path, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        On(method, path, (request, _) => Task.FromResult(respond(request)));

    public void On(HttpMethod method, string path, Func<HttpRequestMessage, int, HttpResponseMessage> respond) =>
        On(method, path, (request, call) => Task.FromResult(respond(request, call)));

    public void On(HttpMethod method, string path, Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond)
    {
        lock (_gate)
        {
            _routes.RemoveAll(r => r.Method == method && r.Path == path);
            _routes.Add(new Route { Method = method, Path = path, Respond = respond });
        }
    }

    public int CallCount(HttpMethod method, string path)
    {
        lock (_gate)
        {
            return Requests.Count(r => r.Method == method && r.Uri.AbsolutePath == path);
        }
    }

    public IReadOnlyList<RecordedRequest> RequestsTo(HttpMethod method, string path)
    {
        lock (_gate)
        {
            return Requests.Where(r => r.Method == method && r.Uri.AbsolutePath == path).ToList();
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Route? route;
        int call;
        lock (_gate)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                body,
                request.Headers.Authorization?.Parameter,
                request.Headers.IfNoneMatch.Count == 0 ? null : string.Join(",", request.Headers.IfNoneMatch.Select(e => e.Tag)),
                request.Headers.UserAgent.ToString()));

            route = _routes.FirstOrDefault(r => r.Method == request.Method && r.Path == request.RequestUri!.AbsolutePath);
            call = route is null ? 0 : route.Calls++;
        }

        if (Throw is not null)
        {
            throw Throw;
        }

        if (route is null)
        {
            return Error(HttpStatusCode.NotFound, "not_found", $"No fake route for {request.Method} {request.RequestUri!.AbsolutePath}");
        }

        var response = await route.Respond(request, call);
        response.RequestMessage ??= request;
        return response;
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Error(HttpStatusCode status, string code, string message) =>
        Json(status, $"{{\"error\":{{\"code\":\"{code}\",\"message\":\"{message}\"}}}}");

    public static HttpResponseMessage NoContent() => new(HttpStatusCode.NoContent);
}
