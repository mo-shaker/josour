using Josour.Core.Control;
using Josour.Infrastructure.Api;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Josour.Infrastructure.Contacts;

/// <summary>
/// The signed-in user's contacts as the app knows them (ADR-0014): who they are and what each side has allowed, from
/// <c>GET /contacts</c>; pending invitations, from <c>GET /contacts/invites</c>; who is online now, from
/// <c>contacts.presence</c>; and which of their devices are offering a connection, from <c>hosts.snapshot</c>/<c>hosts.update</c>.
/// </summary>
public interface IContactsStore
{
    IReadOnlyList<ContactDto> Contacts { get; }

    InvitesDto Invites { get; }

    /// <summary>At least one of the contact's devices is connected now.</summary>
    bool IsOnline(Guid userId);

    /// <summary>The contact's devices that are available to share their connection right now.</summary>
    IReadOnlyList<HostInfoDto> HostsOf(Guid userId);

    /// <summary>I let this contact use my connection without asking me (<c>i_trust</c>).</summary>
    bool Trusts(Guid userId);

    /// <summary>Raised (on any thread) after any of the above changed.</summary>
    event Action? Changed;

    /// <summary>Re-reads the contacts and the invitations over REST. Never throws for a server that cannot be reached.</summary>
    Task RefreshAsync(CancellationToken ct);

    /// <summary>Forgets everything (sign-out).</summary>
    void Clear();
}

/// <summary>
/// The store is fed by the control channel and refreshed over REST: <c>contacts.changed</c> and every <c>hello.ack</c>
/// re-read the lists (the frame carries nothing itself, so it can never disagree with REST); <c>contacts.presence</c> and
/// <c>hosts.*</c> are applied as they arrive.
/// </summary>
public sealed class ContactsStore : IContactsStore, IDisposable
{
    private readonly IApiClient _api;
    private readonly IControlChannel _channel;
    private readonly ILogger<ContactsStore> _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private IReadOnlyList<ContactDto> _contacts = [];
    private InvitesDto _invites = InvitesDto.Empty;
    private Dictionary<Guid, bool> _online = new();
    private Dictionary<Guid, IReadOnlyList<HostInfoDto>> _hosts = new();
    private int _pendingRefreshes;

    public ContactsStore(IApiClient api, IControlChannel channel, ILogger<ContactsStore>? logger = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _logger = logger ?? NullLogger<ContactsStore>.Instance;
        _channel.MessageReceived += OnMessage;
    }

    public event Action? Changed;

    public IReadOnlyList<ContactDto> Contacts
    {
        get
        {
            lock (_gate)
            {
                return _contacts;
            }
        }
    }

    public InvitesDto Invites
    {
        get
        {
            lock (_gate)
            {
                return _invites;
            }
        }
    }

    public bool IsOnline(Guid userId)
    {
        lock (_gate)
        {
            if (_online.TryGetValue(userId, out var online))
            {
                return online;
            }

            return _contacts.FirstOrDefault(c => c.UserId == userId)?.Online ?? false;
        }
    }

    public IReadOnlyList<HostInfoDto> HostsOf(Guid userId)
    {
        lock (_gate)
        {
            return _hosts.TryGetValue(userId, out var hosts) ? hosts : [];
        }
    }

    public bool Trusts(Guid userId)
    {
        lock (_gate)
        {
            return _contacts.FirstOrDefault(c => c.UserId == userId)?.ITrust ?? false;
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        // Several contacts.changed in a row collapse into the refresh already running plus at most one more.
        if (Interlocked.Increment(ref _pendingRefreshes) > 2)
        {
            Interlocked.Decrement(ref _pendingRefreshes);
            return;
        }

        await _refreshing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var contacts = _api.GetContactsAsync(ct);
            var invites = _api.GetInvitesAsync(ct);
            var fresh = await contacts.ConfigureAwait(false);
            var pending = await invites.ConfigureAwait(false);
            lock (_gate)
            {
                _contacts = fresh;
                _invites = pending;
                // REST is newer than any presence frame that arrived before it.
                _online = fresh.ToDictionary(c => c.UserId, c => c.Online);
            }

            _logger.LogInformation(
                "Contacts refreshed: {Contacts} contacts, {Incoming} incoming and {Outgoing} outgoing invitations",
                fresh.Count,
                pending.Incoming.Count,
                pending.Outgoing.Count);
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            _logger.LogWarning("Could not refresh contacts: {Message}", ex.Message);
        }
        finally
        {
            _refreshing.Release();
            Interlocked.Decrement(ref _pendingRefreshes);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _contacts = [];
            _invites = InvitesDto.Empty;
            _online = new();
            _hosts = new();
        }

        Changed?.Invoke();
    }

    private void OnMessage(ControlMessage message)
    {
        switch (message)
        {
            case HelloAckMessage:
            case ContactsChangedMessage:
                _ = RefreshAsync(CancellationToken.None);
                break;

            case ContactsPresenceMessage presence:
                lock (_gate)
                {
                    if (presence.Full)
                    {
                        // Everyone the snapshot leaves out is offline - not "whatever REST last said".
                        _online = _contacts.ToDictionary(c => c.UserId, _ => false);
                    }

                    foreach (var entry in presence.Contacts)
                    {
                        _online[entry.UserId] = entry.Online;
                    }
                }

                Changed?.Invoke();
                break;

            case HostsMessage hosts:
                lock (_gate)
                {
                    _hosts = hosts.Hosts
                        .Where(h => h.UserId != Guid.Empty)
                        .GroupBy(h => h.UserId)
                        .ToDictionary(g => g.Key, g => (IReadOnlyList<HostInfoDto>)g.ToList());
                }

                Changed?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        _channel.MessageReceived -= OnMessage;
        _refreshing.Dispose();
    }
}
