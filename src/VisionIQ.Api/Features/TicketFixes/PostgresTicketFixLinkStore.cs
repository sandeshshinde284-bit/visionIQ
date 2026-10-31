using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using VisionIQ.Api.Configuration;

namespace VisionIQ.Api.Features.TicketFixes;

/// <summary>Records the trust level of a source-observed ticket/fix relationship.</summary>
public enum TicketFixSourceClassification
{
    Unknown,
    Inferred,
    Explicit,
}

/// <summary>Represents the two permissible outcomes of an auditable human review.</summary>
public enum TicketFixReviewOutcome
{
    Verified,
    Disputed,
}

/// <summary>
/// A provider-scoped association. Source references must be immutable evidence IDs
/// rather than guesses derived from timestamps or textual similarity.
/// </summary>
public sealed record TicketFixClaim(
    string SourceInstance,
    string ProjectId,
    string TicketId,
    string? RepositoryId,
    string? PullRequestId,
    string? CommitSha,
    TicketFixSourceClassification Classification,
    IReadOnlyList<string> SourceEvidenceRefs,
    IReadOnlyList<string> RevertCommitShas)
{
    /// <summary>Returns an unambiguous length-prefixed SHA-256 identity for the association.</summary>
    public string AssociationKey()
    {
        Validate();
        using var buffer = new MemoryStream();
        foreach (var field in new[]
            {
                SourceInstance, ProjectId, TicketId, RepositoryId, PullRequestId, CommitSha,
            })
        {
            Span<byte> length = stackalloc byte[4];
            if (field is null)
            {
                BinaryPrimitives.WriteInt32LittleEndian(length, -1);
                buffer.Write(length);
                continue;
            }

            var data = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32LittleEndian(length, data.Length);
            buffer.Write(length);
            buffer.Write(data);
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    /// <summary>Rejects unscoped, oversized or untraceable source claims.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceInstance) || SourceInstance.Length > 1024 ||
            string.IsNullOrWhiteSpace(ProjectId) || ProjectId.Length > 256 ||
            string.IsNullOrWhiteSpace(TicketId) || TicketId.Length > 256 ||
            (RepositoryId is { Length: > 256 }) || (PullRequestId is { Length: > 256 }) ||
            (CommitSha is { Length: > 256 }) ||
            (PullRequestId is null && CommitSha is null))
        {
            throw new ArgumentException("Ticket-fix association must have valid source scope and fix identity.");
        }

        ArgumentNullException.ThrowIfNull(SourceEvidenceRefs);
        ArgumentNullException.ThrowIfNull(RevertCommitShas);
        if (Classification == TicketFixSourceClassification.Explicit &&
            SourceEvidenceRefs.Count == 0)
        {
            throw new ArgumentException("Explicit claims require at least one source evidence reference.");
        }

        if (SourceEvidenceRefs.Concat(RevertCommitShas)
            .Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 1024))
        {
            throw new ArgumentException("Evidence references must be nonempty and bounded.");
        }
    }
}

/// <summary>
/// Stores source-observed associations without overwriting review decisions.
/// No public HTTP route exposes this store or unverified source data.
/// </summary>
/// <summary>Persistence boundary for source-declared fixes and observed reverts.</summary>
public interface ITicketFixLinkStore
{
    /// <summary>Idempotently persists a scoped source association without changing its review verdict.</summary>
    Task<string> UpsertSourceAsync(
        TicketFixClaim claim, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a full-SHA, source-observed revert for existing links in the same
    /// provider, project and repository; never infers an ambiguous short SHA.
    /// </summary>
    Task<int> RecordRevertAsync(
        string sourceInstance, string projectId, string repositoryId,
        string originalCommitSha, string revertCommitSha,
        CancellationToken cancellationToken = default);
}

public sealed class PostgresTicketFixLinkStore : ITicketFixLinkStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource source;

    /// <summary>Creates a pooled data source under the limited API database role.</summary>
    public PostgresTicketFixLinkStore(DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = options.Host,
            Port = options.Port,
            Database = options.Name,
            Username = options.User,
            Password = options.Password,
            Timeout = 10,
        };
        builder["SSL Mode"] = options.SslMode;
        source = NpgsqlDataSource.Create(builder.ConnectionString);
    }

    /// <summary>
    /// Inserts or idempotently refreshes a source claim. Previously verified or disputed
    /// reviews are never silently cleared by another connector synchronization.
    /// </summary>
    public async Task<string> UpsertSourceAsync(
        TicketFixClaim claim,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var key = claim.AssociationKey();
        const string sql = """
            INSERT INTO evidence.ticket_fix_links (
                association_key, source_instance, project_id, ticket_id, repository_id,
                pull_request_id, commit_sha, source_classification,
                source_evidence_refs, revert_commit_shas
            ) VALUES (
                @key, @source, @project, @ticket, @repo,
                @pr, @commit, @classification, @evidence, @reverts
            )
            ON CONFLICT (association_key) DO UPDATE SET
                last_seen_at = now(),
                source_classification = CASE
                    WHEN EXCLUDED.source_classification = 'explicit' THEN 'explicit'
                    ELSE evidence.ticket_fix_links.source_classification END,
                source_evidence_refs = (
                    SELECT COALESCE(jsonb_agg(DISTINCT e.value ORDER BY e.value), '[]'::jsonb)
                    FROM jsonb_array_elements_text(
                        evidence.ticket_fix_links.source_evidence_refs ||
                        EXCLUDED.source_evidence_refs
                    ) AS e(value)
                ),
                revert_commit_shas = (
                    SELECT COALESCE(jsonb_agg(DISTINCT r.value ORDER BY r.value), '[]'::jsonb)
                    FROM jsonb_array_elements_text(
                        evidence.ticket_fix_links.revert_commit_shas ||
                        EXCLUDED.revert_commit_shas
                    ) AS r(value)
                )
            """;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("source", claim.SourceInstance);
        command.Parameters.AddWithValue("project", claim.ProjectId);
        command.Parameters.AddWithValue("ticket", claim.TicketId);
        command.Parameters.AddWithValue("repo", (object?)claim.RepositoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("pr", (object?)claim.PullRequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("commit", (object?)claim.CommitSha ?? DBNull.Value);
        command.Parameters.AddWithValue("classification", claim.Classification.ToString().ToLowerInvariant());
        command.Parameters.Add(new NpgsqlParameter("evidence", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(claim.SourceEvidenceRefs.Distinct(StringComparer.Ordinal)),
        });
        command.Parameters.Add(new NpgsqlParameter("reverts", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(claim.RevertCommitShas.Distinct(StringComparer.OrdinalIgnoreCase)),
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
        return key;
    }

    /// <summary>
    /// Appends a reviewed decision in one transaction, rejecting conflicting replay under the
    /// same reference and rejecting verification of an already-reverted association.
    /// </summary>
    public async Task<TicketFixReviewOutcome> RecordReviewAsync(
        string associationKey,
        TicketFixReviewOutcome outcome,
        string reviewReference,
        CancellationToken cancellationToken = default)
    {
        if (associationKey.Length != 64 || !associationKey.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Association identity must be a 64-character SHA-256 key.", nameof(associationKey));
        }

        if (string.IsNullOrWhiteSpace(reviewReference) || reviewReference.Length > 1024)
        {
            throw new ArgumentException("A bounded review evidence reference is required.", nameof(reviewReference));
        }

        var requested = outcome.ToString().ToLowerInvariant();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var link = new NpgsqlCommand("""
            SELECT revert_commit_shas
            FROM evidence.ticket_fix_links WHERE association_key = @key FOR UPDATE
            """, connection, transaction);
        link.Parameters.AddWithValue("key", associationKey);
        var reverted = (string?)await link.ExecuteScalarAsync(cancellationToken);
        if (reverted is null)
        {
            throw new InvalidOperationException("Cannot review a nonexistent source association.");
        }

        await using var prior = new NpgsqlCommand("""
            SELECT result FROM evidence.ticket_fix_reviews
            WHERE association_key = @key AND review_reference = @reference
            """, connection, transaction);
        prior.Parameters.AddWithValue("key", associationKey);
        prior.Parameters.AddWithValue("reference", reviewReference);
        var old = (string?)await prior.ExecuteScalarAsync(cancellationToken);
        if (old is not null)
        {
            if (!string.Equals(old, requested, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Review reference already records a different decision.");
            }

            await transaction.CommitAsync(cancellationToken);
            return outcome;
        }

        using (var array = JsonDocument.Parse(reverted))
        {
            if (outcome == TicketFixReviewOutcome.Verified &&
                array.RootElement.GetArrayLength() > 0)
            {
                throw new InvalidOperationException("A reverted fix cannot be verified without new source evidence.");
            }
        }

        await using var insert = new NpgsqlCommand("""
            INSERT INTO evidence.ticket_fix_reviews (
                review_id, association_key, review_reference, result
            ) VALUES (@id, @key, @reference, @result)
            """, connection, transaction);
        insert.Parameters.AddWithValue("id", Guid.NewGuid());
        insert.Parameters.AddWithValue("key", associationKey);
        insert.Parameters.AddWithValue("reference", reviewReference);
        insert.Parameters.AddWithValue("result", requested);
        await insert.ExecuteNonQueryAsync(cancellationToken);

        await using var update = new NpgsqlCommand("""
            UPDATE evidence.ticket_fix_links
            SET review_status = @result, last_seen_at = now()
            WHERE association_key = @key
            """, connection, transaction);
        update.Parameters.AddWithValue("result", requested);
        update.Parameters.AddWithValue("key", associationKey);
        await update.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }

    /// <summary>
    /// Adds a verified-by-source full-SHA revert marker to matching scoped links.
    /// An existing review remains in the audit; readers must treat a reverted
    /// association as non-current even if an earlier review said verified.
    /// </summary>
    public async Task<int> RecordRevertAsync(
        string sourceInstance, string projectId, string repositoryId,
        string originalCommitSha, string revertCommitSha,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstance);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        if (!IsFullSha(originalCommitSha) || !IsFullSha(revertCommitSha) ||
            string.Equals(originalCommitSha, revertCommitSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Revert markers require two different complete 40-character Git SHAs.");
        }

        const string sql = """
            UPDATE evidence.ticket_fix_links
            SET revert_commit_shas = (
                SELECT COALESCE(jsonb_agg(DISTINCT value ORDER BY value), '[]'::jsonb)
                FROM jsonb_array_elements_text(
                    revert_commit_shas || jsonb_build_array(@revert)
                ) AS entries(value)
            ), last_seen_at = now()
            WHERE source_instance = @source AND project_id = @project
              AND repository_id = @repository
              AND lower(commit_sha) = lower(@original)
            """;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("source", sourceInstance);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("repository", repositoryId);
        command.Parameters.AddWithValue("original", originalCommitSha);
        command.Parameters.AddWithValue("revert", revertCommitSha.ToLowerInvariant());
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Accepts only canonical full-length hexadecimal Git object IDs.</summary>
    private static bool IsFullSha(string? sha) =>
        sha is { Length: 40 } && sha.All(Uri.IsHexDigit);

    /// <summary>Closes the underlying Npgsql connection pool.</summary>
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
