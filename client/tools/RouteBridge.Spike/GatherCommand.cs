using System.Text.Json;
using RouteBridge.Core.Control;
using RouteBridge.Core.Tunnel;
using RouteBridge.Tunnel.Candidates;
using RouteBridge.Tunnel.Diagnostics;

namespace RouteBridge.Spike;

public static class GatherCommand
{
    public static async Task<int> RunAsync(Args args, CancellationToken ct)
    {
        var port = args.RequireInt("port");
        var options = new GatherOptions(
            ListenPort: port,
            BackendPublicIp: args.Get("public-ip"),
            SamePublicIp: args.Has("same-public-ip"),
            MappingLifetime: TimeSpan.FromMinutes(args.GetInt("lifetime-min", 10)),
            EnableUpnp: !args.Has("no-upnp"));

        Console.Error.WriteLine($"gathering candidates for port {port} (upnp={(options.EnableUpnp ? "on" : "off")}, same_public_ip={options.SamePublicIp}) ...");
        await using var gatherer = new CandidateGatherer();
        var result = await gatherer.GatherAsync(options, ct);
        var host = await HostDiagnostics.CollectAsync(ct);

        var output = new Dictionary<string, object?>
        {
            ["candidates"] = result.Candidates.Select(ToDto).ToList(),
            ["gather_diagnostics"] = result.Diagnostics.ToDictionary(),
            ["host_diagnostics"] = host,
        };
        Console.WriteLine(JsonSerializer.Serialize(output, Json.Pretty));

        if (gatherer.HasMapping)
        {
            var removed = await gatherer.RemoveMappingAsync(CancellationToken.None);
            Console.Error.WriteLine($"upnp mapping removed: {removed}");
        }
        return 0;
    }

    public static CandidateDto ToDto(CandidateEndpoint c) => new(CandidateTypeNames.ToWire(c.Type), c.Ip, c.Port);
}
