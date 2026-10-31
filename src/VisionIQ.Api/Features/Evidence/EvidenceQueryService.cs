using System.Text.Json;
using Npgsql;
using VisionIQ.Api.Configuration;

namespace VisionIQ.Api.Features.Evidence;

/// <summary>
/// Trusted requester context supplied by application authentication, never
/// populated directly from unverified request bodies or caller-defined headers.
/// </summary>
public sealed record EvidenceRequester(
    string PrincipalId, IReadOnlySet<string> GroupPrincipalIds)
{
    /// <summary>All allowed requester identities with empty values removed.</summary>
    public IReadOnlySet<string> PrincipalIds()
    {
        ArgumentNullException.ThrowIfNull(GroupPrincipalIds);
        var ids = new HashSet<string>(GroupPrincipalIds, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(PrincipalId))
        {
            ids.Add(PrincipalId);
        }

        return ids;
    }
}

/// <summary>A single latest canonical source revision, with redacted metadata only.</summary>
public sealed record EvidenceReadCandidate(
    EvidenceIdentity Identity,
    string ProviderRevisionId,
    string SourceUrl,
    string RedactedJson,
    bool AclVerified,
    IReadOnlyList<string> PrincipalIds);

/// <summary>
/// Authorized, redacted evidence without internal ACL principal metadata or
/// Cloud Storage archive locations. Suitable for a future authenticated caller.
/// </summary>
public sealed record AuthorizedEvidence(
    EvidenceIdentity Identity, string ProviderRevisionId,
    string SourceUrl, string RedactedJson);

/// <summary>Provides read-only access to the latest canonical source revision.</summary>
public interface ICanonicalEvidenceReader
{
    /// <summary>Reads the current canonical revision or returns null when unavailable.</summary>
    Task<EvidenceReadCandidate?> FindLatestAsync(
        string itemKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Revalidates current source-provider access using an authenticated identity.
/// Implementations MUST verify the provider's current rights, not cached ACLs.
/// </summary>
public interface IProviderAccessVerifier
{
    /// <summary>
    /// Returns true only when an authenticated requester can currently read
    /// this exact source identity, including its project and ancestor policies.
    /// </summary>
    Task<bool> CanReadNowAsync(
        EvidenceIdentity identity, EvidenceRequester requester,
        CancellationToken cancellationToken = default);
}

/// <summary>Secure default: no provider is assumed public or currently authorized.</summary>
public sealed class DenyUnknownSourcePermissionVerifier : IProviderAccessVerifier
{
    /// <summary>Denies all accesses until a reviewed provider-specific verifier is installed.</summary>
    public Task<bool> CanReadNowAsync(
        EvidenceIdentity identity, EvidenceRequester requester,
        CancellationToken cancellationToken = default) => Task.FromResult(false);
}

/// <summary>
/// Internal fail-closed evidence query boundary for future retrieval and
/// agent features. Neither source text nor archive URLs are ever returned.
/// </summary>
public sealed class EvidenceQueryService(
    ICanonicalEvidenceReader reader,
    IProviderAccessVerifier providerAccess)
{
    /// <summary>
    /// Returns safe metadata only if the revision's stored ACL is verified,
    /// the authenticated caller belongs to an allowed principal, and the
    /// source provider independently verifies current access.
    /// </summary>
    public async Task<AuthorizedEvidence?> FindLatestAuthorizedAsync(
        string itemKey, EvidenceRequester requester,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemKey);
        ArgumentNullException.ThrowIfNull(requester);
        if (itemKey.Length != 64 || !itemKey.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Evidence keys must be canonical SHA-256 values.", nameof(itemKey));
        }

        var principalIds = requester.PrincipalIds();
        if (principalIds.Count == 0)
        {
            return null;
        }

        var candidate = await reader.FindLatestAsync(itemKey, cancellationToken)
            .ConfigureAwait(false);
        if (candidate is null || !candidate.AclVerified ||
            !candidate.PrincipalIds.Any(principalIds.Contains))
        {
            return null;
        }

        if (!await providerAccess.CanReadNowAsync(
            candidate.Identity, requester, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // Re-check candidate metadata even after provider callback: never return
        // an unverified revision or raw payload to the caller.
        return new AuthorizedEvidence(candidate.Identity, candidate.ProviderRevisionId,
            candidate.SourceUrl, candidate.RedactedJson);
    }
}

/// <summary>
/// Least-privilege read-only PostgreSQL adapter for versioned redacted evidence.
/// The immutable Cloud Storage archive is not accessed by this query.
/// </summary>
public sealed class PostgresCanonicalEvidenceReader : ICanonicalEvidenceReader, IAsyncDisposable
{
    private readonly NpgsqlDataSource source;

    /// <summary>Opens a pooled connection under the application's restricted identity.</summary>
    public PostgresCanonicalEvidenceReader(DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = options.Host, Port = options.Port, Database = options.Name,
            Username = options.User, Password = options.Password, Timeout = 10,
        };
        builder["SSL Mode"] = options.SslMode;
        source = NpgsqlDataSource.Create(builder.ConnectionString);
    }

    /// <summary>
    /// Loads only safe, redacted latest-revision metadata and the stored
    /// principals required for a separate current-provider permission check.
    /// </summary>
    public async Task<EvidenceReadCandidate?> FindLatestAsync(
        string itemKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemKey);
        const string sql = """
            SELECT i.provider, i.source_instance, i.project_id, i.repository_id,
                   i.kind, i.external_id, r.provider_revision_id, r.source_url,
                   r.redacted_payload::text, r.acl_verified, r.acl_principals::text
            FROM evidence.source_items AS i
            INNER JOIN evidence.source_revisions AS r
              ON r.item_key = i.item_key AND r.provider_revision_id = i.latest_revision_id
            WHERE i.item_key = @item
            """;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("item", itemKey);
        await using var result = await command.ExecuteReaderAsync(cancellationToken);
        if (!await result.ReadAsync(cancellationToken))
        {
            return null;
        }

        var identity = new EvidenceIdentity(
            result.GetString(0), result.GetString(1), result.GetString(2),
            result.IsDBNull(3) ? null : result.GetString(3), result.GetString(4),
            result.GetString(5));
        var aclJson = result.GetString(10);
        using var principals = JsonDocument.Parse(aclJson);
        var ids = principals.RootElement.ValueKind == JsonValueKind.Array
            ? principals.RootElement.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!)
                .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()
            : [];
        return new EvidenceReadCandidate(identity, result.GetString(6),
            result.GetString(7), result.GetString(8), result.GetBoolean(9), ids);
    }

    /// <summary>Closes the Npgsql pool.</summary>
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
