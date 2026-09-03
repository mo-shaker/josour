namespace RouteBridge.Core.Tunnel;

/// <summary>أسماء أنواع المرشحين على السلك كما في docs/ws-protocol.md (session.endpoint / session.peer_endpoint).</summary>
public static class CandidateTypeNames
{
    public static string ToWire(CandidateType type) => type switch
    {
        CandidateType.Lan => "lan",
        CandidateType.V6 => "v6",
        CandidateType.Upnp => "upnp",
        CandidateType.Public => "public",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "unknown candidate type"),
    };

    public static bool TryParse(string? wire, out CandidateType type)
    {
        switch (wire?.Trim().ToLowerInvariant())
        {
            case "lan": type = CandidateType.Lan; return true;
            case "v6": type = CandidateType.V6; return true;
            case "upnp": type = CandidateType.Upnp; return true;
            case "public": type = CandidateType.Public; return true;
            default: type = default; return false;
        }
    }

    public static CandidateType Parse(string wire)
        => TryParse(wire, out var type) ? type : throw new FormatException($"Unknown candidate type '{wire}'.");
}
