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
    IReadOnlyCollection<string> approvedProjects) : IProviderAccessVerifier
{
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
            // A source outage or unreadable membership never grants access.
            return false;
        }
    }
}

/// <summary>Sends each evidence item to its provider's verifier; unknown providers are denied.</summary>
public sealed class ProviderRoutingAccessVerifier(IReadOnlyDictionary<string, IProviderAccessVerifier> verifiers)
    : IProviderAccessVerifier
{
    public Task<bool> CanReadNowAsync(EvidenceIdentity identity, EvidenceRequester requester, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return verifiers.TryGetValue(identity.Provider, out var verifier)
            ? verifier.CanReadNowAsync(identity, requester, cancellationToken)
            : Task.FromResult(false);
    }
}
