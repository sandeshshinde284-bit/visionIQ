using VisionIQ.Ingestor.Ado;

namespace VisionIQ.Api.Features.Evidence;

/// <summary>
/// N-17: code-scan evidence (files, symbols, tests) belongs to the ADO project it was scanned for; it is readable
/// by people who are currently members of that project, checked live. Only approved projects; any error denies.
/// </summary>
public sealed class CodeScanAccessVerifier(IAdoProjectMembership membership, IReadOnlyCollection<string> approvedProjects, ILogger? logger = null)
    : IProviderAccessVerifier
{
    private readonly ILogger log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private readonly HashSet<string> projects = new(approvedProjects, StringComparer.OrdinalIgnoreCase);

    public async Task<bool> CanReadNowAsync(EvidenceIdentity identity, EvidenceRequester requester, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(requester);
        if (identity.Provider != "git" || identity.Kind != "code_scan" ||
            !projects.Contains(identity.ProjectId) || string.IsNullOrWhiteSpace(requester.PrincipalId))
        {
            VisionIQ.Api.Logging.AccessLog.Denied(log, identity.Provider, identity.Kind, "not code of an approved project, or no user identity");
            return false;
        }

        try
        {
            var member = await membership.IsMemberAsync(identity.ProjectId, requester.PrincipalId, cancellationToken).ConfigureAwait(false);
            if (!member)
            {
                VisionIQ.Api.Logging.AccessLog.Denied(log, identity.Provider, identity.Kind, "requester is not a current member of project " + identity.ProjectId);
            }

            return member;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            VisionIQ.Api.Logging.AccessLog.CheckFailed(log, "git", error.GetType().Name);
            return false;
        }
    }
}
