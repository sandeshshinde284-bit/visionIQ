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
/// <summary>N-2: hides source items that were deleted in, or are no longer readable from, their source.</summary>
public interface IEvidenceTombstones
{
    /// <summary>False when nothing changed (unknown item, already tombstoned, or a newer revision exists).</summary>
    Task<bool> TombstoneAsync(string itemKey, DateTimeOffset observedAt, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Review R1-03: item keys of the same external id and kind, stored under a different project than
    /// <paramref name="currentProjectId"/>, not already tombstoned. A work item moved to another ADO
    /// project stops matching its old project's WIQL filter, so this is the only way the old copy is
    /// ever found again; used to tombstone it (reason "missing_in_source") once the item is observed
    /// under its new project.
    /// </summary>
    Task<IReadOnlyList<string>> FindOtherProjectItemKeysAsync(
        string provider, string sourceInstance, string kind, string externalId,
        string currentProjectId, CancellationToken cancellationToken = default);
}

/// <summary>
/// PostgreSQL-backed implementation of the versioned evidence store: items and revisions are
/// append-only (no UPDATE ever changes revision content), so every write here is an INSERT or
/// a careful conditional UPDATE of "latest" pointers, never an overwrite of stored history.
/// </summary>
public sealed class PostgresEvidenceStore : IVersionedEvidenceStore, IEvidenceTombstones, IAsyncDisposable
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

    /// <summary>N-7: re-observed content becomes latest again when it ties the latest on source time.</summary>
    public const string PromoteReobservedSql = """
        UPDATE evidence.source_items AS item
        SET latest_revision_id = @revision, latest_observed_at = @observed
        WHERE item.item_key = @key
          AND item.latest_revision_id <> @revision
          AND NOT (item.latest_revision_id ~ '^[0-9]+$' AND @revision ~ '^[0-9]+$')
          AND item.latest_source_updated_at = (
              SELECT seen.source_updated_at FROM evidence.source_revisions AS seen
              WHERE seen.item_key = @key AND seen.provider_revision_id = @revision)
          -- Newer than the last time the CURRENT latest was confirmed, so a stale observation never wins.
          AND (item.latest_observed_at IS NULL OR item.latest_observed_at < @observed)
        """;

    /// <summary>N-2: a newer source revision than the tombstone clears it (e.g. the item was restored).</summary>
    public const string ClearTombstoneSql = """
        UPDATE evidence.source_items
        SET deleted_at = NULL, deletion_reason = NULL
        WHERE item_key = @key AND deleted_at IS NOT NULL AND deleted_at < @source_updated
        """;

    /// <summary>
    /// N-2: tombstone an item; never one that already has a source revision newer than the deletion
    /// observation (it was restored in between).
    /// </summary>
    public const string TombstoneSql = """
        UPDATE evidence.source_items
        SET deleted_at = @at, deletion_reason = @reason
        WHERE item_key = @key AND deleted_at IS NULL
          AND (latest_source_updated_at IS NULL OR latest_source_updated_at <= @at)
        """;

    public async Task<bool> TombstoneAsync(string itemKey, DateTimeOffset observedAt, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemKey);
        if (reason is not ("deleted_in_source" or "missing_in_source"))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(TombstoneSql, connection);
        command.Parameters.AddWithValue("key", itemKey);
        command.Parameters.AddWithValue("at", observedAt);
        command.Parameters.AddWithValue("reason", reason);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Review R1-03: finds the stored item(s) this external id used to live under, elsewhere.</summary>
    public const string OtherProjectItemKeysSql = """
        SELECT item_key FROM evidence.source_items
        WHERE provider = @provider AND source_instance = @instance AND kind = @kind
          AND external_id = @external_id AND project_id <> @project AND deleted_at IS NULL
        """;

    public async Task<IReadOnlyList<string>> FindOtherProjectItemKeysAsync(
        string provider, string sourceInstance, string kind, string externalId,
        string currentProjectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstance);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentProjectId);

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(OtherProjectItemKeysSql, connection);
        command.Parameters.AddWithValue("provider", provider);
        command.Parameters.AddWithValue("instance", sourceInstance);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("external_id", externalId);
        command.Parameters.AddWithValue("project", currentProjectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var keys = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
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
        // xmax = 0 is Postgres's own tell for "this row was just INSERTed, not touched by the
        // ON CONFLICT branch" -- that's how Created vs. NewRevision is decided below without a
        // separate round trip.

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
            if (oldHash.TrimEnd() == evidence.ContentSha256)
            {
                // N-7: content that changed A -> B -> back to A (same source time) is seen again now, so the
                // source currently states A: promote it back to latest. Numeric ADO revisions never tie.
                await using var promote = new NpgsqlCommand(PromoteReobservedSql, connection, transaction);
                promote.Parameters.AddWithValue("key", evidence.ItemKey);
                promote.Parameters.AddWithValue("revision", input.ProviderRevisionId);
                promote.Parameters.AddWithValue("observed", input.ObservedAt);
                await promote.ExecuteNonQueryAsync(cancellationToken);
            }

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

        // N-2: a revision stated after the item was tombstoned (e.g. restored in ADO) clears the tombstone.
        await using (var restore = new NpgsqlCommand(ClearTombstoneSql, connection, transaction))
        {
            restore.Parameters.AddWithValue("key", evidence.ItemKey);
            restore.Parameters.AddWithValue("source_updated", input.SourceUpdatedAt);
            await restore.ExecuteNonQueryAsync(cancellationToken);
        }

        const string latestSql = """
            UPDATE evidence.source_items
            SET latest_revision_id = @revision,
                latest_source_updated_at = @source_updated,
                latest_observed_at = @observed
            WHERE item_key = @key AND (
                latest_revision_id IS NULL OR
                latest_source_updated_at < @source_updated OR
                -- B-8: revisions sharing a source timestamp. Digit-only IDs (ADO revisions)
                -- compare numerically via (length, text) so "10" > "9" without a cast.
                -- B-9: other IDs (content revisions "native#hash") are ordered by when they
                -- were first observed, then by ordinal text as a final deterministic tie-break.
                (latest_source_updated_at = @source_updated AND (
                    CASE WHEN latest_revision_id ~ '^[0-9]+$' AND @revision ~ '^[0-9]+$'
                        THEN (length(latest_revision_id), latest_revision_id COLLATE "C")
                           < (length(@revision), @revision COLLATE "C")
                        ELSE (
                            SELECT (current.observed_at, current.provider_revision_id COLLATE "C")
                                 < (@observed, @revision COLLATE "C")
                            FROM evidence.source_revisions AS current
                            WHERE current.item_key = @key
                              AND current.provider_revision_id = source_items.latest_revision_id)
                    END))
            )
            """;
        await using var latest = new NpgsqlCommand(latestSql, connection, transaction);
        latest.Parameters.AddWithValue("key", evidence.ItemKey);
        latest.Parameters.AddWithValue("revision", input.ProviderRevisionId);
        latest.Parameters.AddWithValue("source_updated", input.SourceUpdatedAt);
        latest.Parameters.AddWithValue("observed", input.ObservedAt);
        await latest.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new EvidenceImportResult(
            evidence.ItemKey,
            input.ProviderRevisionId,
            created ? EvidenceImportStatus.Created : EvidenceImportStatus.NewRevision);
    }

    public ValueTask DisposeAsync() => source.DisposeAsync();
}
