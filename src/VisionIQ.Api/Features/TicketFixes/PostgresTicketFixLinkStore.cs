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
    /// <summary>
    /// Review R4-03: ADO stated the relationship (a pull request lists this ticket), but the pull
    /// request has not completed, so it has not changed the target branch and is not a fix. Kept,
    /// not discarded, so an active or abandoned PR still shows as associated development work; every
    /// reader that treats a ticket-fix link as proof of a fix already requires 'explicit' or
    /// 'inferred' (retrieval.py, lesson_store.py, TicketFixGraphMaterializer.cs), so these rows are
    /// excluded from "fix" consumers without any change on their side. Upgrades to Explicit, by the
    /// same upsert the completed pull request's commit(s) then provide, the first time it completes.
    /// </summary>
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
        Span<byte> length = stackalloc byte[4]; // N-18: allocated once, not per loop iteration
        foreach (var field in new[]
            {
                SourceInstance, ProjectId, TicketId, RepositoryId, PullRequestId, CommitSha,
            })
        {
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
    /// Third-pass correction, Finding #1: identical to <see cref="UpsertSourceAsync"/> except it never
    /// clears an existing retraction. For circumstantial or candidate evidence (e.g. a PR-title revert
    /// match) that must never be able to resurrect an association a newer authoritative source has
    /// already retracted -- unlike a direct, explicit source-stated claim, which is always fresh
    /// evidence and may re-link.
    /// </summary>
    Task<string> UpsertCandidateEvidenceAsync(
        TicketFixClaim claim, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a full-SHA, source-observed revert for existing links in the same
    /// provider, project and repository; never infers an ambiguous short SHA.
    /// </summary>
    Task<int> RecordRevertAsync(
        string sourceInstance, string projectId, string repositoryId,
        string originalCommitSha, string revertCommitSha,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Review R4-02: replaces one source entity's (a work item's or a pull request's) complete
    /// current set of claims, retracting any association that entity previously asserted and no
    /// longer does, unless another known source still asserts it. <paramref name="isCompleteSnapshot"/>
    /// must be true only when every field <paramref name="claims"/> was built from was fetched in
    /// full for this exact revision -- never on a partial read. Also rejects an out-of-order or
    /// replayed revision, compared by the source's own revision time.
    /// </summary>
    Task ReconcileSourceAsync(
        string sourceItemKey, string sourceRevisionId, DateTimeOffset sourceRevisionTime,
        bool isCompleteSnapshot, IReadOnlyList<TicketFixClaim> claims,
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
        // This is called for direct, authoritative source claims: e.g., when ADO tells us
        // "this PR explicitly links this ticket" or "this commit appeared in this PR".
        // These claims are trusted enough to resurrect a previously-retracted link (if the
        // retraction is gone from the source). The guarantee is idempotency: calling this
        // twice with the same claim produces the same result, and human reviews stay untouched.
        ArgumentNullException.ThrowIfNull(claim);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // A direct, explicit source-stated claim (the only caller of this public method) is always
        // fresh evidence and may clear a prior retraction (re-linking): it carries no revision-replay
        // risk, unlike the entity-level reconciliation in ReconcileSourceAsync below.
        var key = await UpsertCoreAsync(connection, transaction, claim, clearRetraction: true, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken);
        return key;
    }

    /// <summary>
    /// Third-pass correction, Finding #1: as <see cref="UpsertSourceAsync"/>, but never clears an
    /// existing retraction -- for circumstantial/candidate evidence that must not be able to reactivate
    /// an association a newer authoritative source has already retracted.
    /// </summary>
    public async Task<string> UpsertCandidateEvidenceAsync(
        TicketFixClaim claim,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var key = await UpsertCoreAsync(connection, transaction, claim, clearRetraction: false, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken);
        return key;
    }

    /// <summary>
    /// Finding #4 (second-pass correction): appends one immutable row to
    /// <c>evidence.ticket_fix_link_lifecycle_events</c> for a retraction or a relink (clearing a
    /// retraction). Never updates or deletes a prior row, so a link that cycles through retract and
    /// relink more than once keeps every transition on the record, not just the current state that
    /// <c>ticket_fix_links.retracted_at</c> alone can show.
    /// </summary>
    private static async Task RecordLifecycleEventAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string associationKey, string eventType, string? reason, CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand("""
            INSERT INTO evidence.ticket_fix_link_lifecycle_events (association_key, event_type, reason)
            VALUES (@key, @event_type, @reason)
            """, connection, transaction);
        insert.Parameters.AddWithValue("key", associationKey);
        insert.Parameters.AddWithValue("event_type", eventType);
        insert.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// The write at the heart of <see cref="UpsertSourceAsync"/>, usable within a caller-owned
    /// transaction. Review R4-02 (second-pass correction): <paramref name="clearRetraction"/> must be
    /// decided by the caller from its own freshness check, never unconditionally -- an out-of-order
    /// or replayed revision must still be able to record its claim as historical evidence without
    /// reactivating a retraction a newer revision already established.
    /// </summary>
    private static async Task<string> UpsertCoreAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        TicketFixClaim claim, bool clearRetraction, CancellationToken cancellationToken)
    {
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
                -- Review R4-04: revert_commit_shas is left untouched here (never merged from
                -- @reverts, which every caller passes as empty anyway) -- RecomputeRevertStateAsync
                -- below is the one place that sets it, from the full current observation history,
                -- not from whatever this one claim happened to carry.
                -- Review R4-02: cleared only when the caller has confirmed this write is current
                -- (@clear_retraction); a review's verified/disputed verdict is a separate column and
                -- is never touched here either way.
                retracted_at = CASE WHEN @clear_retraction THEN NULL ELSE evidence.ticket_fix_links.retracted_at END,
                retraction_reason = CASE WHEN @clear_retraction THEN NULL ELSE evidence.ticket_fix_links.retraction_reason END
            """;
        // Serialize observations and association creation within one source/repo/SHA scope.
        // Otherwise a concurrent first-time revert could be missed by both operations.
        var hasCommitScope = claim.RepositoryId is not null && IsFullSha(claim.CommitSha);
        if (hasCommitScope)
        {
            await LockRevertScopeAsync(connection, transaction, claim.SourceInstance,
                claim.ProjectId, claim.RepositoryId!, claim.CommitSha!, cancellationToken);
        }

        // Finding #4 (second-pass correction): capture whether this write will actually clear an
        // existing retraction, so the transition can be appended to the lifecycle event log below --
        // the CASE in @sql always re-writes retracted_at/retraction_reason to NULL when
        // @clear_retraction is true, even when they were already NULL, so this is the only point
        // that can tell a real transition from a no-op.
        var wasRetracted = false;
        if (clearRetraction)
        {
            await using var pre = new NpgsqlCommand(
                "SELECT retracted_at IS NOT NULL FROM evidence.ticket_fix_links WHERE association_key = @key",
                connection, transaction);
            pre.Parameters.AddWithValue("key", key);
            wasRetracted = await pre.ExecuteScalarAsync(cancellationToken) is true;
        }

        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("key", key);
            command.Parameters.AddWithValue("source", claim.SourceInstance);
            command.Parameters.AddWithValue("project", claim.ProjectId);
            command.Parameters.AddWithValue("ticket", claim.TicketId);
            command.Parameters.AddWithValue("repo", (object?)claim.RepositoryId ?? DBNull.Value);
            command.Parameters.AddWithValue("pr", (object?)claim.PullRequestId ?? DBNull.Value);
            command.Parameters.AddWithValue("commit", (object?)claim.CommitSha ?? DBNull.Value);
            command.Parameters.AddWithValue("classification", claim.Classification.ToString().ToLowerInvariant());
            command.Parameters.AddWithValue("clear_retraction", clearRetraction);
            command.Parameters.Add(new NpgsqlParameter("evidence", NpgsqlDbType.Jsonb)
            {
                Value = JsonSerializer.Serialize(claim.SourceEvidenceRefs.Distinct(StringComparer.Ordinal)),
            });
            command.Parameters.Add(new NpgsqlParameter("reverts", NpgsqlDbType.Jsonb)
            {
                Value = JsonSerializer.Serialize(claim.RevertCommitShas.Distinct(StringComparer.OrdinalIgnoreCase)),
            });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (wasRetracted)
        {
            await RecordLifecycleEventAsync(connection, transaction, key, "cleared", null, cancellationToken)
                .ConfigureAwait(false);
        }

        // Review R4-04: a brand-new claim may be created for a commit that already has (possibly
        // chained) revert history; fold in the current, correctly-cancelled-if-applicable state
        // rather than the raw, un-cancelled observation list.
        if (hasCommitScope)
        {
            await RecomputeRevertStateAsync(connection, transaction, claim.SourceInstance,
                claim.ProjectId, claim.RepositoryId!, claim.CommitSha!.ToLowerInvariant(), cancellationToken)
                .ConfigureAwait(false);
        }

        return key;
    }

    /// <summary>
    /// Review R4-02: replaces one source entity's (a work item's or a pull request's) complete
    /// current set of claims. <paramref name="isCompleteSnapshot"/> must be true only when every
    /// field <paramref name="claims"/> was built from was read successfully and in full for this
    /// exact <paramref name="sourceRevisionId"/> (never on a partial fetch) -- including when that
    /// means <paramref name="claims"/> is legitimately empty (the source currently asserts nothing).
    /// When true and the revision is at least as new as the one already recorded for this source (the
    /// same not-newer-than-recorded guard <c>PostgresTestLinkStore.ReplaceAsync</c> uses, keyed on the
    /// source's own revision time, never our processing clock): every claim is upserted, any
    /// association this exact source previously asserted and no longer does is retracted (marked,
    /// never deleted) unless another known source still asserts it, and the association's
    /// <c>provenance_status</c> becomes 'reconciled'. When false (not a complete snapshot, or an
    /// out-of-order / replayed revision): claims are still upserted as historical evidence, but
    /// nothing is retracted and no association is marked reconciled from this call.
    /// </summary>
    public async Task ReconcileSourceAsync(
        string sourceItemKey, string sourceRevisionId, DateTimeOffset sourceRevisionTime,
        bool isCompleteSnapshot, IReadOnlyList<TicketFixClaim> claims,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceItemKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRevisionId);
        ArgumentNullException.ThrowIfNull(claims);

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Decided BEFORE any claim is upserted (second-pass correction): an out-of-order or partial
        // revision must never clear a retraction that a later, already-processed revision established.
        var accepted = isCompleteSnapshot && await AcceptRevisionAsync(
            connection, transaction, sourceItemKey, sourceRevisionId, sourceRevisionTime, cancellationToken)
            .ConfigureAwait(false);

        var keys = new List<string>(claims.Count);
        foreach (var claim in claims)
        {
            keys.Add(await UpsertCoreAsync(connection, transaction, claim, clearRetraction: accepted, cancellationToken)
                .ConfigureAwait(false));
        }

        if (accepted)
        {
            await ReplaceSourceAssociationsAsync(
                connection, transaction, sourceItemKey, keys, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// False (no-op, guard row left untouched) when a revision no later than the one already recorded
    /// for this source is replayed -- the same not-newer-than-recorded guard
    /// <c>PostgresTestLinkStore.ReplaceAsync</c> uses, so an out-of-order delivery can never assert
    /// "this is now the current state". Compares the source's own revision time, never our processing
    /// clock, so detection time (when we noticed) stays independent of the source's actual change time.
    /// </summary>
    /// <remarks>
    /// Second-pass correction (independent review, cross-checked against R1-06): a pull request's
    /// <c>LastActivity</c> (its only available ordering signal) is <c>ClosedDate ?? CreatedDate</c>, so
    /// it never changes while the PR stays open -- but R1-06 re-enriches and re-checks every open PR
    /// on every run regardless. With a strict "time must increase" guard, the *first* re-check of an
    /// open PR is accepted and every one after it is silently rejected forever, even when a new commit
    /// was pushed, because the time never moves. Accepting an equal time when the content-hash
    /// revision id differs closes that gap while an out-of-order *older* revision (a real backfill
    /// replay, where the time strictly decreases) is still rejected exactly as before.
    /// </remarks>
    private static async Task<bool> AcceptRevisionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sourceItemKey, string sourceRevisionId, DateTimeOffset sourceRevisionTime,
        CancellationToken cancellationToken)
    {
        await using var guard = new NpgsqlCommand("""
            INSERT INTO evidence.ticket_fix_link_sources
                (source_item_key, source_revision_id, source_revision_time)
            VALUES (@item, @revision, @at)
            ON CONFLICT (source_item_key) DO UPDATE SET
                source_revision_id = EXCLUDED.source_revision_id,
                source_revision_time = EXCLUDED.source_revision_time,
                updated_at = now()
            WHERE evidence.ticket_fix_link_sources.source_revision_time < EXCLUDED.source_revision_time
               OR (evidence.ticket_fix_link_sources.source_revision_time = EXCLUDED.source_revision_time
                   AND evidence.ticket_fix_link_sources.source_revision_id <> EXCLUDED.source_revision_id)
            RETURNING 1
            """, connection, transaction);
        guard.Parameters.AddWithValue("item", sourceItemKey);
        guard.Parameters.AddWithValue("revision", sourceRevisionId);
        guard.Parameters.AddWithValue("at", sourceRevisionTime);
        return await guard.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>
    /// Records that <paramref name="sourceItemKey"/> currently asserts exactly <paramref name="keys"/>
    /// (per exact <c>association_key</c> -- a work item's own relation list and a pull request's own
    /// list produce different keys even for "the same" ticket/PR pair, by design, and are reconciled
    /// independently), and retracts (marked via <c>retracted_at</c>, never deleted) any association it
    /// previously asserted and no longer does, as long as no other known source still actively asserts
    /// it. Review R4-02 (second-pass correction): locks the candidate <c>ticket_fix_links</c> rows with
    /// <c>FOR UPDATE</c> before deciding, so a concurrent reconciliation from a <em>different</em>
    /// source touching the same association is serialized rather than racing; "no other source"
    /// membership is a soft, re-openable state (<c>ended_at</c>), never a deleted row.
    /// </summary>
    private static async Task ReplaceSourceAssociationsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sourceItemKey, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        var current = keys.Distinct(StringComparer.Ordinal).ToArray();

        var candidates = new List<string>();
        await using (var find = new NpgsqlCommand("""
            SELECT association_key FROM evidence.ticket_fix_link_source_associations
            WHERE source_item_key = @item AND ended_at IS NULL AND NOT (association_key = ANY(@current))
            """, connection, transaction))
        {
            find.Parameters.AddWithValue("item", sourceItemKey);
            find.Parameters.Add(new NpgsqlParameter("current", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = current });
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add(reader.GetString(0));
            }
        }

        if (candidates.Count > 0)
        {
            var candidateArray = candidates.ToArray();
            // Serializes with any other transaction deciding the same association's retraction at
            // the same time (a different source's ReconcileSourceAsync call racing this one).
            await using (var lockRows = new NpgsqlCommand("""
                SELECT 1 FROM evidence.ticket_fix_links WHERE association_key = ANY(@keys) FOR UPDATE
                """, connection, transaction))
            {
                lockRows.Parameters.Add(new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = candidateArray });
                await using var locked = await lockRows.ExecuteReaderAsync(cancellationToken);
                while (await locked.ReadAsync(cancellationToken))
                {
                }
            }

            // Re-decided under lock: a concurrent source may have asserted one of these in the gap
            // between the read above and acquiring the lock.
            var stillOtherwiseAsserted = new HashSet<string>(StringComparer.Ordinal);
            await using (var recheck = new NpgsqlCommand("""
                SELECT DISTINCT association_key FROM evidence.ticket_fix_link_source_associations
                WHERE association_key = ANY(@keys) AND source_item_key <> @item AND ended_at IS NULL
                """, connection, transaction))
            {
                recheck.Parameters.Add(new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = candidateArray });
                recheck.Parameters.AddWithValue("item", sourceItemKey);
                await using var reader = await recheck.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    stillOtherwiseAsserted.Add(reader.GetString(0));
                }
            }

            var retractable = candidates.Where(key => !stillOtherwiseAsserted.Contains(key)).ToArray();
            if (retractable.Length > 0)
            {
                await using var retract = new NpgsqlCommand("""
                    UPDATE evidence.ticket_fix_links
                    SET retracted_at = now(), retraction_reason = 'missing_in_source'
                    WHERE association_key = ANY(@keys) AND retracted_at IS NULL
                    """, connection, transaction);
                retract.Parameters.Add(new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = retractable });
                await retract.ExecuteNonQueryAsync(cancellationToken);

                // Finding #4 (second-pass correction): append one event per newly-retracted
                // association, so a later relink (which clears retracted_at) does not erase the
                // fact that this retraction happened.
                foreach (var retractedKey in retractable)
                {
                    await RecordLifecycleEventAsync(connection, transaction, retractedKey, "retracted",
                        "missing_in_source", cancellationToken).ConfigureAwait(false);
                }
            }

            // This source no longer vouches for these -- soft transition (never deleted), whether or
            // not another source keeps them visible.
            await using (var end = new NpgsqlCommand("""
                UPDATE evidence.ticket_fix_link_source_associations
                SET ended_at = now()
                WHERE source_item_key = @item AND ended_at IS NULL AND association_key = ANY(@keys)
                """, connection, transaction))
            {
                end.Parameters.AddWithValue("item", sourceItemKey);
                end.Parameters.Add(new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = candidateArray });
                await end.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        foreach (var key in current)
        {
            // Reopens a previously ended row for the same (source, association) pair if it recurs,
            // keeping the original first_asserted_at rather than losing it to a fresh insert.
            await using (var assert = new NpgsqlCommand("""
                INSERT INTO evidence.ticket_fix_link_source_associations (source_item_key, association_key)
                VALUES (@item, @key)
                ON CONFLICT (source_item_key, association_key) DO UPDATE SET ended_at = NULL
                """, connection, transaction))
            {
                assert.Parameters.AddWithValue("item", sourceItemKey);
                assert.Parameters.AddWithValue("key", key);
                await assert.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var reconcile = new NpgsqlCommand("""
                UPDATE evidence.ticket_fix_links SET provenance_status = 'reconciled'
                WHERE association_key = @key AND provenance_status <> 'reconciled'
                """, connection, transaction))
            {
                reconcile.Parameters.AddWithValue("key", key);
                await reconcile.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        // A zero-claim complete snapshot (current.Length == 0) still reaches here and still retracts
        // every previously-asserted-by-this-source candidate above: there is no early return on an
        // empty current set, so "this source now asserts nothing" is treated as real information, not
        // skipped (review R4-02 point: legacy/zero-link snapshots must be processed, not special-cased).
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
    /// <summary>
    /// Review R4-04: records that <paramref name="revertCommitSha"/> reverts
    /// <paramref name="originalCommitSha"/>, then recomputes the *current* revert state -- not a
    /// simple append -- for <paramref name="originalCommitSha"/> and every commit whose state can
    /// depend on it (everything it, in turn, reverts, transitively). A revert of a revert is
    /// recognized and cancels the earlier revert's effect; a further revert of that re-applies it.
    /// Returns the total number of <c>ticket_fix_links</c> rows whose stored revert state changed.
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

        // The revert observation must exist even when the target association has not
        // yet been ingested. Replay of the original PR/commit will then attach it.
        const string observationSql = """
            INSERT INTO evidence.ticket_fix_revert_observations (
                source_instance, project_id, repository_id,
                original_commit_sha, revert_commit_sha
            ) VALUES (@source, @project, @repository, @original, @revert)
            ON CONFLICT (source_instance, project_id, repository_id,
                         original_commit_sha, revert_commit_sha)
            DO UPDATE SET last_seen_at = now()
            """;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var original = originalCommitSha.ToLowerInvariant();
        var revert = revertCommitSha.ToLowerInvariant();
        // Known, accepted scope limitation: this serializes concurrent writers against the
        // revert's own original commit, not every commit the recompute below may touch further up
        // a chain -- deep concurrent revert chains on the same scope are rare, and a serialized
        // RecordRevertAsync caller (the sink already processes one PR/commit write at a time) makes
        // a real collision unlikely in practice.
        await LockRevertScopeAsync(connection, transaction, sourceInstance, projectId,
            repositoryId, original, cancellationToken);

        await using (var observed = new NpgsqlCommand(observationSql, connection, transaction))
        {
            observed.Parameters.AddWithValue("source", sourceInstance);
            observed.Parameters.AddWithValue("project", projectId);
            observed.Parameters.AddWithValue("repository", repositoryId);
            observed.Parameters.AddWithValue("original", original);
            observed.Parameters.AddWithValue("revert", revert);
            await observed.ExecuteNonQueryAsync(cancellationToken);
        }

        var updated = await RecomputeRevertStateAsync(
            connection, transaction, sourceInstance, projectId, repositoryId, original, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    /// <summary>
    /// Review R4-04: finds every commit whose revert state can depend on <paramref name="commit"/>'s
    /// (itself, plus everything <paramref name="commit"/> transitively reverts -- walking "up" through
    /// <c>ticket_fix_revert_observations</c> via <c>original_commit_sha</c> where <paramref name="commit"/>
    /// appears as the <c>revert_commit_sha</c>), recomputes each one's current, possibly-cancelled
    /// revert state, and writes the result. Returns the number of <c>ticket_fix_links</c> rows updated.
    /// </summary>
    private static async Task<int> RecomputeRevertStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sourceInstance, string projectId, string repositoryId, string commit,
        CancellationToken cancellationToken)
    {
        var affected = await FindAffectedCommitsAsync(
            connection, transaction, sourceInstance, projectId, repositoryId, commit, cancellationToken)
            .ConfigureAwait(false);

        var memo = new Dictionary<string, (bool Reverted, string[] Witnesses)>(StringComparer.Ordinal);
        var inProgress = new HashSet<string>(StringComparer.Ordinal);
        var updated = 0;
        foreach (var affectedCommit in affected)
        {
            var (_, witnesses) = await ComputeCurrentRevertStateAsync(
                connection, transaction, sourceInstance, projectId, repositoryId,
                affectedCommit, memo, inProgress, cancellationToken).ConfigureAwait(false);
            updated += await ApplyRevertStateAsync(
                connection, transaction, sourceInstance, projectId, repositoryId,
                affectedCommit, witnesses, cancellationToken).ConfigureAwait(false);
        }

        return updated;
    }

    /// <summary>
    /// Review R4-04: {<paramref name="start"/>} plus every commit <paramref name="start"/> directly
    /// or transitively reverts, found by walking "original_commit_sha" backwards from
    /// "revert_commit_sha = start". Bounded (100 hops, visited-set) against a malformed or cyclic
    /// observation history -- a real revert chain is never remotely that deep.
    /// </summary>
    private static async Task<IReadOnlyList<string>> FindAffectedCommitsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sourceInstance, string projectId, string repositoryId, string start,
        CancellationToken cancellationToken)
    {
        var affected = new List<string> { start };
        var visited = new HashSet<string>(StringComparer.Ordinal) { start };
        var queue = new Queue<string>();
        queue.Enqueue(start);
        var hops = 0;
        while (queue.Count > 0 && hops < 100)
        {
            var current = queue.Dequeue();
            hops++;
            await using var command = new NpgsqlCommand("""
                SELECT DISTINCT original_commit_sha FROM evidence.ticket_fix_revert_observations
                WHERE source_instance = @source AND project_id = @project
                  AND repository_id = @repository AND revert_commit_sha = @revert
                """, connection, transaction);
            command.Parameters.AddWithValue("source", sourceInstance);
            command.Parameters.AddWithValue("project", projectId);
            command.Parameters.AddWithValue("repository", repositoryId);
            command.Parameters.AddWithValue("revert", current);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var parent = reader.GetString(0);
                if (visited.Add(parent))
                {
                    affected.Add(parent);
                    queue.Enqueue(parent);
                }
            }
        }

        return affected;
    }

    /// <summary>
    /// Review R4-04: true (with the current standing reverter(s) as witnesses) when at least one
    /// commit that reverts <paramref name="commit"/> has not itself been reverted. Recursive: a
    /// reverter that has itself been reverted does not count, and a revert of <em>that</em> revert
    /// re-applies the original effect. <paramref name="inProgress"/> defensively breaks a cycle in
    /// malformed data (treated as "not currently reverted" rather than recursing forever).
    /// </summary>
    private static async Task<(bool Reverted, string[] Witnesses)> ComputeCurrentRevertStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sourceInstance, string projectId, string repositoryId, string commit,
        Dictionary<string, (bool Reverted, string[] Witnesses)> memo, HashSet<string> inProgress,
        CancellationToken cancellationToken)
    {
        if (memo.TryGetValue(commit, out var cached))
        {
            return cached;
        }

        if (!inProgress.Add(commit))
        {
            return (false, []); // cycle guard: never observed in real revert history
        }

        var reverters = new List<string>();
        await using (var command = new NpgsqlCommand("""
            SELECT DISTINCT revert_commit_sha FROM evidence.ticket_fix_revert_observations
            WHERE source_instance = @source AND project_id = @project
              AND repository_id = @repository AND original_commit_sha = @commit
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("source", sourceInstance);
            command.Parameters.AddWithValue("project", projectId);
            command.Parameters.AddWithValue("repository", repositoryId);
            command.Parameters.AddWithValue("commit", commit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                reverters.Add(reader.GetString(0));
            }
        }

        var witnesses = new List<string>();
        foreach (var reverter in reverters)
        {
            var (reverterReverted, _) = await ComputeCurrentRevertStateAsync(
                connection, transaction, sourceInstance, projectId, repositoryId,
                reverter, memo, inProgress, cancellationToken).ConfigureAwait(false);
            if (!reverterReverted)
            {
                witnesses.Add(reverter); // this reverter stands uncancelled
            }
        }

        inProgress.Remove(commit);
        var result = (witnesses.Count > 0, witnesses.Order(StringComparer.Ordinal).ToArray());
        memo[commit] = result;
        return result;
    }

    /// <summary>Writes the recomputed, possibly-empty, current witness set (never appended to).</summary>
    private static async Task<int> ApplyRevertStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sourceInstance, string projectId, string repositoryId, string commit,
        string[] witnesses, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE evidence.ticket_fix_links
            SET revert_commit_shas = @witnesses::jsonb, last_seen_at = now()
            WHERE source_instance = @source AND project_id = @project
              AND repository_id = @repository AND lower(commit_sha) = @commit
              AND revert_commit_shas <> @witnesses::jsonb
            """, connection, transaction);
        command.Parameters.AddWithValue("source", sourceInstance);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("repository", repositoryId);
        command.Parameters.AddWithValue("commit", commit);
        command.Parameters.Add(new NpgsqlParameter("witnesses", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(witnesses),
        });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Uses the same transaction-scoped lock in revert and fix ingestion, so neither
    /// operation can finish between the other's observation lookup and insert.
    /// </summary>
    private static async Task LockRevertScopeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sourceInstance, string projectId, string repositoryId,
        string originalCommitSha, CancellationToken cancellationToken)
    {
        // Length prefixes prevent ambiguity even if scope identifiers contain separators.
        var parts = new[] { sourceInstance, projectId, repositoryId, originalCommitSha.ToLowerInvariant() };
        var scope = string.Concat(parts.Select(part => part.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ":" + part));
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@scope, 0))", connection, transaction);
        command.Parameters.AddWithValue("scope", scope);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Accepts only canonical full-length hexadecimal Git object IDs.</summary>
    private static bool IsFullSha(string? sha) =>
        sha is { Length: 40 } && sha.All(Uri.IsHexDigit);

    /// <summary>Closes the underlying Npgsql connection pool.</summary>
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
