using Josour.Core.Control;

namespace Josour.Infrastructure.Session;

/// <summary>What the UI needs to know about the current session (everything from <c>session.created</c> except the secret).</summary>
public sealed record SessionInfo(
    Guid SessionId,
    string Role,
    DateTimeOffset ExpiresAt,
    string PeerUserDisplayName,
    string PeerDeviceName,
    string PeerPublicIp,
    bool SamePublicIp,
    int AllowlistVersion)
{
    public const string HostRole = "host";
    public const string GuestRole = "guest";

    public bool IsHost => string.Equals(Role, HostRole, StringComparison.Ordinal);

    public static SessionInfo From(SessionCreatedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new SessionInfo(
            message.SessionId,
            message.Role,
            message.ExpiresAt,
            message.Peer?.UserDisplayName ?? string.Empty,
            message.Peer?.DeviceName ?? string.Empty,
            message.PeerPublicIp,
            message.SamePublicIp,
            message.AllowlistVersion);
    }
}
