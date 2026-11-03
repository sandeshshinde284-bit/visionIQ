using VisionIQ.Api.Features.Evidence;
using VisionIQ.Ingestor.Confluence;

namespace VisionIQ.Ingestor.Cli;

/// <summary>
/// Checks present-day Confluence view restrictions using the already-approved
/// read-only connector, including every ancestor restriction. The requester
/// identity and membership MUST originate from trusted server-side identity.
/// </summary>
/// <summary>
/// B-7: whether a trusted requester can view a Confluence space. Page restrictions only narrow
/// space permissions, so an unrestricted page is visible only to people with space access.
/// </summary>
public interface IConfluenceSpaceAccess
{
    Task<bool> CanViewSpaceAsync(string spaceKey, EvidenceRequester requester, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default until space permissions are read from Confluence (for example Cloud's content
/// permission-check API): nobody is assumed to have space access, so every page stays hidden.
/// </summary>
public sealed class DenyUnverifiedSpaceAccess : IConfluenceSpaceAccess
{
    public Task<bool> CanViewSpaceAsync(string spaceKey, EvidenceRequester requester, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}

public sealed class ConfluenceCurrentAccessVerifier(
    IConfluenceSource source,
    string sourceInstance,
    IConfluenceSpaceAccess? spaceAccess = null) : IProviderAccessVerifier
{
    private readonly IConfluenceSpaceAccess spaces = spaceAccess ?? new DenyUnverifiedSpaceAccess();

    /// <summary>
    /// Re-reads the page ACL rather than trusting an old stored snapshot.
    /// Any missing page, unknown ancestor, failed API call or unapproved
    /// space fails closed. Other providers require separate implementations.
    /// </summary>
    public async Task<bool> CanReadNowAsync(
        EvidenceIdentity identity,
        EvidenceRequester requester,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(requester);
        if (!string.Equals(identity.Provider, "confluence", StringComparison.Ordinal) ||
            !string.Equals(identity.SourceInstance, sourceInstance, StringComparison.Ordinal) ||
            !source.SpaceKeys.Contains(identity.ProjectId, StringComparer.Ordinal) ||
            !new[] { "design_page", "design_decision", "confluence_acl" }
                .Contains(identity.Kind, StringComparer.Ordinal))
        {
            return false;
        }

        try
        {
            var pages = await source.GetPagesAsync(identity.ProjectId, cancellationToken)
                .ConfigureAwait(false);
            var acls = ConfluenceAccess.ComputeAcls(pages);
            if (!acls.TryGetValue(identity.ExternalId, out var acl))
            {
                return false;
            }

            // B-7: space membership must be verified, never assumed from the page being asked about.
            if (!await spaces.CanViewSpaceAsync(identity.ProjectId, requester, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            var trustedViewer = ConfluenceViewer.Create(
                requester.PrincipalId, requester.GroupPrincipalIds, [identity.ProjectId]);
            return ConfluenceAccess.CanView(acl, trustedViewer);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A source outage can never grant access through a stale cached ACL.
            return false;
        }
    }
}
