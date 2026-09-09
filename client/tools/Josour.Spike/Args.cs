using System.Globalization;

namespace Josour.Spike;

public sealed class UsageException : Exception
{
    public UsageException(string message) : base(message) { }
}

/// <summary>تحليل يدوي بسيط: --name value أو --flag.</summary>
public sealed class Args
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public Args(string[] args, int start)
    {
        for (var i = start; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"unexpected argument '{arg}'");
            var name = arg[2..];
            if (name.Length == 0) throw new UsageException("empty option name");
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                _values[name] = args[++i];
            }
            else
            {
                _values[name] = null;
            }
        }
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;

    public string Require(string name)
        => _values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : throw new UsageException($"--{name} is required");

    public int GetInt(string name, int defaultValue)
    {
        var v = Get(name);
        if (v is null) return defaultValue;
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : throw new UsageException($"--{name} must be an integer");
    }

    public int RequireInt(string name)
    {
        var v = Require(name);
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : throw new UsageException($"--{name} must be an integer");
    }
}

public static class Json
{
    public static readonly System.Text.Json.JsonSerializerOptions Pretty = new() { WriteIndented = true };
    public static readonly System.Text.Json.JsonSerializerOptions Compact = new() { WriteIndented = false };
}

/// <summary>زوج NetworkStream على loopback للاختبارات الذاتية.</summary>
public static class Loopback
{
    public static async Task<(Stream Server, Stream Client)> CreatePairAsync(CancellationToken ct)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptSocketAsync(ct).AsTask();
            var client = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port, ct);
            var server = await acceptTask;
            return (new System.Net.Sockets.NetworkStream(server, true), new System.Net.Sockets.NetworkStream(client, true));
        }
        finally
        {
            listener.Stop();
        }
    }
}
