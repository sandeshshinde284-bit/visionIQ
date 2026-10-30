using Npgsql;
using NpgsqlTypes;
using VisionIQ.Api.Configuration;

namespace VisionIQ.Api.Features.Evidence;

public interface IVersionedEvidenceStore
{
    Task<EvidenceImportResult> SaveAsync(
        ArchivedEvidence evidence,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runtime API identity writes only to the evidence schema; migrations are
/// performed separately using the established FND-03 Alembic chain.
/// </summary>
public sealed class PostgresEvidenceStore : IVersionedEvidenceStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource source;

    public PostgresEvidenceStore(DatabaseOptions options)
    {
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

    public async Task<EvidenceImportResult> SaveAsync(
        ArchivedEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        var input = evidence.Envelope;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string itemSql = """
            INSERT INTO evidence.source_items (
                item_key, provider, source_instance, project_id, repository_id,
                kind, external_id, first_observed_at, last_observed_at
            )
            VALUES (@key, @provider, @instance, @project, @repo,
                    @kind, @external, @observed, @observed)
            ON CONFLICT (item_key) DO UPDATE SET
                last_observed_at =
                    GREATEST(evidence.source_items.last_observed_at, EXCLUDED.last_observed_at)
            RETURNING (xmax = 0)
            """;

        await using var itemCommand = new NpgsqlCommand(itemSql, connection, transaction);
        itemCommand.Parameters.AddWithValue("key", evidence.ItemKey);
        itemCommand.Parameters.AddWithValue("provider", input.Identity.Provider);
        itemCommand.Parameters.AddWithValue("instance", input.Identity.SourceInstance);
        itemCommand.Parameters.AddWithValue("project", input.Identity.ProjectId);
        itemCommand.Parameters.AddWithValue("repo", (object?)input.Identity.RepositoryId ?? DBNull.Value);
        itemCommand.Parameters.AddWithValue("kind", input.Identity.Kind);
        itemCommand.Parameters.AddWithValue("external", input.Identity.ExternalId);
        itemCommand.Parameters.AddWithValue("observed", input.ObservedAt);
        var created = (bool)(await itemCommand.ExecuteScalarAsync(cancellationToken) ?? false);

        // Upsert above takes a row lock, serializing concurrent imports of one
        // source item. A native revision ID must never change its contents.
        const string oldRevisionSql = """
            SELECT content_sha256
            FROM evidence.source_revisions
            WHERE item_key = @key AND provider_revision_id = @revision
            """;
        await using var lookup = new NpgsqlCommand(oldRevisionSql, connection, transaction);
        lookup.Parameters.AddWithValue("key", evidence.ItemKey);
        lookup.Parameters.AddWithValue("revision", input.ProviderRevisionId);
        var oldHash = (string?)await lookup.ExecuteScalarAsync(cancellationToken);
        if (oldHash is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new EvidenceImportResult(
                evidence.ItemKey,
                input.ProviderRevisionId,
                oldHash.TrimEnd() == evidence.ContentSha256
                    ? EvidenceImportStatus.Unchanged
                    : EvidenceImportStatus.Conflict);
        }

        const string revisionSql = """
            INSERT INTO evidence.source_revisions (
                item_key, provider_revision_id, source_updated_at, observed_at,
                content_sha256, source_url, retrieved_under_principal,
                acl_verified, acl_principals, redacted_payload,
                redaction_version, normalizer_version, raw_snapshot_uri
            ) VALUES (
                @key, @revision, @source_updated, @observed,
                @hash, @url, @retrieved_by,
                @verified, @acl, @redacted,
                @redaction_version, @normalizer_version, @archive
            )
            """;
        await using var insert = new NpgsqlCommand(revisionSql, connection, transaction);
        insert.Parameters.AddWithValue("key", evidence.ItemKey);
        insert.Parameters.AddWithValue("revision", input.ProviderRevisionId);
        insert.Parameters.AddWithValue("source_updated", input.SourceUpdatedAt);
        insert.Parameters.AddWithValue("observed", input.ObservedAt);
        insert.Parameters.AddWithValue("hash", evidence.ContentSha256);
        insert.Parameters.AddWithValue("url", input.SourceUrl);
        insert.Parameters.AddWithValue("retrieved_by", input.RetrievedUnderPrincipal);
        insert.Parameters.AddWithValue("verified", input.Access.Verified);
        insert.Parameters.Add(new NpgsqlParameter("acl", NpgsqlDbType.Jsonb)
        {
            Value = input.Access.Json(),
        });
        insert.Parameters.Add(new NpgsqlParameter("redacted", NpgsqlDbType.Jsonb)
        {
            Value = evidence.RedactedJson,
        });
        insert.Parameters.AddWithValue("redaction_version", evidence.RedactionVersion);
        insert.Parameters.AddWithValue("normalizer_version", evidence.NormalizerVersion);
        insert.Parameters.AddWithValue("archive", evidence.RawSnapshotUri);
        await insert.ExecuteNonQueryAsync(cancellationToken);

        const string latestSql = """
            UPDATE evidence.source_items
            SET latest_revision_id = @revision,
                latest_source_updated_at = @source_updated
            WHERE item_key = @key AND (
                latest_revision_id IS NULL OR
                latest_source_updated_at < @source_updated
            )
            """;
        await using var latest = new NpgsqlCommand(latestSql, connection, transaction);
        latest.Parameters.AddWithValue("key", evidence.ItemKey);
        latest.Parameters.AddWithValue("revision", input.ProviderRevisionId);
        latest.Parameters.AddWithValue("source_updated", input.SourceUpdatedAt);
        await latest.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new EvidenceImportResult(
            evidence.ItemKey,
            input.ProviderRevisionId,
            created ? EvidenceImportStatus.Created : EvidenceImportStatus.NewRevision);
    }

    public ValueTask DisposeAsync() => source.DisposeAsync();
}
