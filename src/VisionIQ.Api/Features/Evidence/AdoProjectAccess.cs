using VisionIQ.Ingestor.Ado;

namespace VisionIQ.Api.Features.Evidence;

/// <summary>Access principals stored on Azure DevOps evidence (B-5).</summary>
public static class AdoAccessPrincipals
{
    /// <summary>"ado-project:{organization URL}/{project}": everyone in that project may read it.</summary>
    public static string Project(string sourceInstance, string project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstance);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        return "ado-project:" + sourceInstance.TrimEnd('/') + "/" + project;
    }
}

/// <summary>
/// B-6: confirms, at read time, that the requester is still a member of the evidence's ADO project.
/// The requester's <see cref="EvidenceRequester.PrincipalId"/> must be their ADO identity id or unique
/// name, supplied by a trusted server-side caller. Any error denies access.
/// </summary>
public sealed class AdoProjectAccessVerifier(
    IAdoProjectMembership membership,
    string sourceInstance,
    IReadOnlyCollection<string> approvedProjects,
    ILogger? logger = null) : IProviderAccessVerifier
{
    private readonly ILogger log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private readonly string instance = sourceInstance.TrimEnd('/');
    private readonly HashSet<string> projects = new(approvedProjects, StringComparer.OrdinalIgnoreCase);

    public async Task<bool> CanReadNowAsync(EvidenceIdentity identity, EvidenceRequester requester, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(requester);
        if (identity.Provider != "azure_devops" ||
            !string.Equals(identity.SourceInstance.TrimEnd('/'), instance, StringComparison.Ordinal) ||
            !projects.Contains(identity.ProjectId) ||
            string.IsNullOrWhiteSpace(requester.PrincipalId))
        {
            VisionIQ.Api.Logging.AccessLog.Denied(log, identity.Provider, identity.Kind, "not an item of the approved ADO organization/projects, or no user identity");
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
            // A source outage or unreadable membership never grants access.
            VisionIQ.Api.Logging.AccessLog.CheckFailed(log, "azure_devops", error.GetType().Name);
            return false;
        }
    }
}

/// <summary>Sends each evidence item to its provider's verifier; unknown providers are denied.</summary>
public sealed class ProviderRoutingAccessVerifier(IReadOnlyDictionary<string, IProviderAccessVerifier> verifiers, ILogger? logger = null)
    : IProviderAccessVerifier
{
    private readonly ILogger log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public Task<bool> CanReadNowAsync(EvidenceIdentity identity, EvidenceRequester requester, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (verifiers.TryGetValue(identity.Provider, out var verifier))
        {
            return verifier.CanReadNowAsync(identity, requester, cancellationToken);
        }

        VisionIQ.Api.Logging.AccessLog.Denied(log, identity.Provider, identity.Kind, "no access verifier is configured for this provider");
        return Task.FromResult(false);
    }
}
