using System.Security.Claims;
using VisionIQ.Api.Features.Evidence;

namespace VisionIQ.Api.Features.GraphApi;

/// <summary>The outcome of authorizing a validated token; a requester only when allowed.</summary>
public sealed record GraphCallerDecision(bool Allowed, EvidenceRequester? Requester, string Reason);

/// <summary>
/// Decides, from an already signature/issuer/audience/lifetime-validated Entra ID token, whether the call is an
/// approved application acting for a real user, and who that user is. Raw claim names are used
/// (inbound claim mapping is disabled). Every missing or unexpected value denies.
/// </summary>
public sealed class GraphCallerAuthorizer(GraphApiOptions options)
{
    public GraphCallerDecision Authorize(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        string? Claim(string type) => user.FindFirst(type)?.Value?.Trim();

        if (user.Identity?.IsAuthenticated != true)
        {
            return Deny("not authenticated");
        }

        if (!Guid.TryParse(Claim("tid"), out var tenant) || tenant != options.TenantId)
        {
            return Deny("token is from another tenant");
        }

        // Delegated (On-Behalf-Of) tokens carry "scp"; app-only tokens do not and are never accepted here.
        if (Claim("idtyp") is "app" || Claim("scp") is not { Length: > 0 } scopes)
        {
            return Deny("token does not act for a user");
        }

        if (!scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(options.RequiredScope, StringComparer.Ordinal))
        {
            return Deny("required scope missing");
        }

        if ((Claim("azp") ?? Claim("appid")) is not { Length: > 0 } caller || !options.AllowedCallerAppIds.Contains(caller))
        {
            return Deny("calling application is not approved");
        }

        if (Claim("oid") is not { Length: > 0 } ||
            (Claim("preferred_username") ?? Claim("upn")) is not { } name || !name.Contains('@', StringComparison.Ordinal))
        {
            return Deny("user identity missing");
        }

        // The user's sign-in name is their ADO identity; live project membership is still checked per item.
        var projects = options.ApprovedProjects
            .Select(project => AdoAccessPrincipals.Project(options.AdoInstance, project))
            .ToHashSet(StringComparer.Ordinal);
        return new GraphCallerDecision(true, new EvidenceRequester(name.ToLowerInvariant(), projects), "allowed");
    }

    private static GraphCallerDecision Deny(string reason) => new(false, null, reason);
}
