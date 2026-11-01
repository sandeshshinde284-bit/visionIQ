using VisionIQ.Ingestor.Ado;

namespace VisionIQ.Api.Features.Evidence;

/// <summary>
/// N-17: code-scan evidence (files, symbols, tests) belongs to the ADO project it was scanned for; it is readable
/// by people who are currently members of that project, checked live. Only approved projects; any error denies.
/// </summary>
public sealed class CodeScanAccessVerifier(IAdoProjectMembership membership, IReadOnlyCollection<string> approvedProjects)
    : IProviderAccessVerifier
{
    private readonly HashSet<string> projects = new(approvedProjects, StringComparer.OrdinalIgnoreCase);

    public async Task<bool> CanReadNowAsync(EvidenceIdentity identity, EvidenceRequester requester, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(requester);
        if (identity.Provider != "git" || identity.Kind != "code_scan" ||
            !projects.Contains(identity.ProjectId) || string.IsNullOrWhiteSpace(requester.PrincipalId))
        {
            return false;
        }

        try
        {
            return await membership.IsMemberAsync(identity.ProjectId, requester.PrincipalId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
