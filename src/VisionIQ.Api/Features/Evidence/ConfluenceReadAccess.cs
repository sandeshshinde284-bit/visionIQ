using System.Text.Json;
using VisionIQ.Ingestor.Confluence;

namespace VisionIQ.Api.Features.Evidence;

/// <summary>Access principal stored on Confluence page evidence (LRN-02).</summary>
public static class ConfluenceAccessPrincipals
{
    /// <summary>
    /// "confluence-space:{origin}/{space}": candidates only; the live page check decides. The site is reduced to its
    /// origin (scheme + host) so "https://x.atlassian.net" and "https://x.atlassian.net/wiki" match.
    /// </summary>
    public static string Space(string site, string space)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(space);
        return "confluence-space:" + Origin(site) + "/" + space;
    }

    /// <summary>Scheme and host (and non-default port) of a Confluence site address.</summary>
    public static string Origin(string site)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(site);
        var uri = new Uri(site.Contains("://", StringComparison.Ordinal) ? site : "https://" + site);
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }
}

/// <summary>Maps a validated user (sign-in e-mail) to their Atlassian account ID; null denies.</summary>
public interface IConfluenceIdentityResolver
{
    Task<string?> ResolveAccountIdAsync(string principalId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Explicit, administrator-maintained map of e-mail to Atlassian account ID (CONFLUENCE_IDENTITY_MAP_FILE, JSON
/// object). Atlassian does not reliably expose e-mail to account ID lookups, so nothing is guessed: an unmapped
/// user cannot read Confluence evidence.
/// </summary>
public sealed class MappedConfluenceIdentityResolver : IConfluenceIdentityResolver
{
    private readonly Dictionary<string, string> accounts;

    public MappedConfluenceIdentityResolver(IReadOnlyDictionary<string, string> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        accounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (email, account) in map)
        {
            if (!string.IsNullOrWhiteSpace(email) && email.Contains('@', StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(account))
            {
                accounts[email.Trim()] = account.Trim();
            }
        }
    }

    public static MappedConfluenceIdentityResolver FromJson(string json) =>
        new(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? throw new InvalidOperationException("Identity map is empty."));

    public Task<string?> ResolveAccountIdAsync(string principalId, CancellationToken cancellationToken = default) =>
        Task.FromResult(accounts.GetValueOrDefault(principalId?.Trim() ?? string.Empty));
}

/// <summary>
/// LRN-02: a Confluence page is readable only if Confluence itself says this person can read it right now. Cloud's
/// content permission check evaluates space permissions, the page's restrictions and inherited ancestor
/// restrictions, so VisionIQ never re-implements them. Data Center has no such check, so its pages stay hidden.
/// ACL snapshots and unapproved spaces are never readable; any error denies.
/// </summary>
public sealed class ConfluencePageAccessVerifier(ConfluenceHttp confluence, IConfluenceIdentityResolver identities, ILogger? logger = null) : IProviderAccessVerifier
{
    private readonly ILogger log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private readonly string site = ConfluenceAccessPrincipals.Origin(confluence.Options.BaseUrl.AbsoluteUri);
    private readonly HashSet<string> spaces = new(confluence.Options.SpaceKeys, StringComparer.OrdinalIgnoreCase);

    public async Task<bool> CanReadNowAsync(EvidenceIdentity identity, EvidenceRequester requester, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(requester);
        if (identity.Provider != "confluence" || identity.Kind is not ("design_page" or "design_decision") ||
            confluence.Options.Deployment != ConfluenceDeployment.Cloud ||
            !string.Equals(ConfluenceAccessPrincipals.Origin(identity.SourceInstance), site, StringComparison.Ordinal) ||
            !spaces.Contains(identity.ProjectId) ||
            identity.ExternalId is not { Length: > 0 } pageId || !pageId.All(char.IsAsciiDigit) ||
            string.IsNullOrWhiteSpace(requester.PrincipalId))
        {
            VisionIQ.Api.Logging.AccessLog.Denied(log, identity.Provider, identity.Kind,
                confluence.Options.Deployment != ConfluenceDeployment.Cloud
                    ? "Data Center pages are not readable (no permission-check endpoint)"
                    : "not a page of the approved Confluence site/spaces, or no user identity");
            return false;
        }

        try
        {
            if (await identities.ResolveAccountIdAsync(requester.PrincipalId, cancellationToken).ConfigureAwait(false) is not { Length: > 0 } account)
            {
                VisionIQ.Api.Logging.AccessLog.Denied(log, identity.Provider, identity.Kind, "requester is not in the Confluence identity map");
                return false;
            }

            using var answer = await confluence.PostJsonAsync(
                confluence.Api("rest/api/content/" + pageId + "/permission/check"),
                new { subject = new { type = "user", identifier = account }, operation = "read" },
                cancellationToken).ConfigureAwait(false);
            var granted = answer.RootElement.TryGetProperty("hasPermission", out var allowed) && allowed.ValueKind == JsonValueKind.True;
            if (!granted)
            {
                VisionIQ.Api.Logging.AccessLog.Denied(log, identity.Provider, identity.Kind, "Confluence says the requester cannot read this page");
            }

            return granted;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            VisionIQ.Api.Logging.AccessLog.CheckFailed(log, "confluence", error.GetType().Name);
            return false;
        }
    }
}
