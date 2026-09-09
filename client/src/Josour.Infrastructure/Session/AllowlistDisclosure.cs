using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Josour.Infrastructure.Api;

namespace Josour.Infrastructure.Session;

/// <summary>
/// The allow-list as the host is shown it before accepting a request (product document section 15: "المواقع أو الفئات
/// المسموح بها"). <see cref="Loaded"/> is the important part: a list that could not be fetched is NOT an empty list, and
/// the two must never look the same on screen — "nothing is allowed" would make an ordinary request look harmless.
/// </summary>
/// <param name="Version">The <c>allowlist_version</c> of the request this disclosure belongs to.</param>
/// <param name="Sites">The entries of exactly that version, in the server's order; empty when it could not be loaded.</param>
/// <param name="Loaded">True when the server answered with that version.</param>
public sealed record AllowlistDisclosure(
    int Version, IReadOnlyList<string> Sites, bool Loaded, bool Unrestricted = false)
{
    /// <summary>The server could not be asked, or refused: show "could not be loaded", never an empty list.</summary>
    public static AllowlistDisclosure Unavailable(int version) => new(version, Array.Empty<string>(), Loaded: false);

    /// <summary>
    /// The deployment does not enforce the list (ADR-0010), so there is no bounded set of sites to show
    /// and naming one would be a false consent: the guest can reach anything through this host. Distinct
    /// from an empty list on purpose - three states, three sentences, because the host is agreeing to
    /// something different in each.
    /// </summary>
    public static AllowlistDisclosure NoRestriction(int version)
        => new(version, Array.Empty<string>(), Loaded: true, Unrestricted: true);

    /// <summary>The version really does allow nothing (a genuine, and alarming, answer).</summary>
    public bool IsEmpty => Loaded && !Unrestricted && Sites.Count == 0;
}

/// <summary>Resolves an <c>allowlist_version</c> into the sites it allows, for the host's pre-accept disclosure.</summary>
public interface IAllowlistDisclosure
{
    /// <summary>Never throws: a failure comes back as <see cref="AllowlistDisclosure.Unavailable"/>.</summary>
    Task<AllowlistDisclosure> DescribeAsync(int version, CancellationToken ct);
}

/// <summary>
/// <see cref="IAllowlistDisclosure"/> over <c>GET /domains?version=N</c> (docs/api.md), with a per-version cache: the host
/// answers many requests at the same allow-list version and the window must open at once.
/// <para>
/// The fetch gets its own short budget (<see cref="AllowlistDisclosureOptions.Timeout"/>, 5 s) rather than the API client's
/// full 15 s, because the whole request expires in 60 s and the host has to read the disclosure inside that minute. A slow
/// or unreachable server therefore costs the prompt a few seconds, not the decision.
/// </para>
/// <para>
/// It asks for the request's exact version, not the current list: <c>request.incoming</c> names the version the session
/// will run under, and showing today's list for yesterday's version would be a lie. A version the server no longer serves
/// (404) is reported as "could not be loaded" for the same reason.
/// </para>
/// </summary>
public sealed class AllowlistDisclosureService : IAllowlistDisclosure
{
    private readonly IApiClient _api;
    private readonly ILogger _logger;
    private readonly AllowlistDisclosureOptions _options;
    private readonly object _gate = new();
    private AllowlistDisclosure? _cached;

    public AllowlistDisclosureService(IApiClient api, ILogger<AllowlistDisclosureService>? logger = null, AllowlistDisclosureOptions? options = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _options = options ?? AllowlistDisclosureOptions.Default;
    }

    public async Task<AllowlistDisclosure> DescribeAsync(int version, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_cached is { Loaded: true } cached && cached.Version == version)
            {
                return cached;
            }
        }

        if (version < 1)
        {
            // The server never sends this; a malformed frame must not turn into a request for "?version=0".
            _logger.LogWarning("Allow-list version {Version} is not a version: showing the disclosure as unavailable", version);
            return AllowlistDisclosure.Unavailable(version);
        }

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(_options.Timeout);

            var dto = await _api.GetDomainsVersionAsync(version, budget.Token).ConfigureAwait(false);
            if (dto.Version != version)
            {
                _logger.LogWarning("GET /domains?version={Wanted} answered with version {Got}: not showing it as the request's list", version, dto.Version);
                return AllowlistDisclosure.Unavailable(version);
            }

            var disclosure = new AllowlistDisclosure(version, dto.Entries.ToArray(), Loaded: true);
            lock (_gate)
            {
                _cached = disclosure;
            }

            _logger.LogInformation("Allow-list version {Version} for the incoming request: {SiteCount} entries", version, disclosure.Sites.Count);
            return disclosure;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("GET /domains?version={Version} did not answer within {Timeout:0.#} s: the host sees 'could not be loaded'", version, _options.Timeout.TotalSeconds);
            return AllowlistDisclosure.Unavailable(version);
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            _logger.LogWarning(ex, "Could not load allow-list version {Version} for the host's disclosure", version);
            return AllowlistDisclosure.Unavailable(version);
        }
    }
}

/// <summary>Knobs of <see cref="AllowlistDisclosureService"/>; the defaults are what the app ships with.</summary>
public sealed record AllowlistDisclosureOptions
{
    public static AllowlistDisclosureOptions Default { get; } = new();

    /// <summary>How long the disclosure may wait for the server before the prompt opens without the list.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);
}
