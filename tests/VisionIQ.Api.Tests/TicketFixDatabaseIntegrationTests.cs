using Npgsql;
using VisionIQ.Api.Configuration;
using VisionIQ.Api.Features.TicketFixes;

namespace VisionIQ.Api.Tests;

/// <summary>
/// Opt-in PostgreSQL contract test. Run only against disposable CI/local database
/// with completed LRN-04 migration and restricted API-role credentials.
/// </summary>
public sealed class TicketFixDatabaseIntegrationTests
{
    [DatabaseFact("VISIONIQ_TICKET_FIX_DB_INTEGRATION")]
    public async Task Review_history_is_immutable_and_source_replay_does_not_reset_verdict()
    {
        var password = Environment.GetEnvironmentVariable("DB_API_PASSWORD");
        Assert.False(string.IsNullOrWhiteSpace(password));
        var options = new DatabaseOptions(
            Environment.GetEnvironmentVariable("DB_HOST") ?? "127.0.0.1",
            int.TryParse(Environment.GetEnvironmentVariable("DB_PORT"), out var port) ? port : 5432,
            Environment.GetEnvironmentVariable("DB_NAME") ?? "visioniq",
            Environment.GetEnvironmentVariable("DB_API_USER") ?? "visioniq_api",
            password!, "disable");

        await using var store = new PostgresTicketFixLinkStore(options);
        var id = Guid.NewGuid().ToString("N");
        var claim = new TicketFixClaim(
            "test-source", "test-project", id, "repo-one", "pr-one", null,
            TicketFixSourceClassification.Explicit, ["ado:pr_source"], []);
        var key = await store.UpsertSourceAsync(claim);
        Assert.Equal(key, await store.UpsertSourceAsync(claim));
        Assert.Equal(TicketFixReviewOutcome.Verified,
            await store.RecordReviewAsync(key, TicketFixReviewOutcome.Verified, "review-" + id));
        Assert.Equal(TicketFixReviewOutcome.Verified,
            await store.RecordReviewAsync(key, TicketFixReviewOutcome.Verified, "review-" + id));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RecordReviewAsync(key, TicketFixReviewOutcome.Disputed, "review-" + id));
        await store.UpsertSourceAsync(claim);

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = options.Host, Port = options.Port, Database = options.Name,
            Username = options.User, Password = options.Password,
        };
        await using var db = new NpgsqlConnection(builder.ConnectionString);
        await db.OpenAsync();
        await using var query = new NpgsqlCommand("""
            SELECT
                (SELECT count(*) FROM evidence.ticket_fix_links WHERE association_key=@key),
                (SELECT count(*) FROM evidence.ticket_fix_reviews WHERE association_key=@key),
                (SELECT review_status FROM evidence.ticket_fix_links WHERE association_key=@key)
            """, db);
        query.Parameters.AddWithValue("key", key);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal("verified", reader.GetString(2));
    }

    [DatabaseFact("VISIONIQ_TICKET_FIX_DB_INTEGRATION")]
    public async Task Revert_observed_before_a_fix_is_preserved_scoped_and_idempotent()
    {
        var password = Environment.GetEnvironmentVariable("DB_API_PASSWORD");
        Assert.False(string.IsNullOrWhiteSpace(password));
        var options = new DatabaseOptions(
            Environment.GetEnvironmentVariable("DB_HOST") ?? "127.0.0.1",
            int.TryParse(Environment.GetEnvironmentVariable("DB_PORT"), out var port) ? port : 5432,
            Environment.GetEnvironmentVariable("DB_NAME") ?? "visioniq",
            Environment.GetEnvironmentVariable("DB_API_USER") ?? "visioniq_api",
            password!, "disable");

        var uniqueSource = "lrn04-revert-" + Guid.NewGuid().ToString("N");
        var originalSha = new string('a', 40);
        var revertingSha = new string('b', 40);
        var unrelatedSha = new string('c', 40);
        const string project = "lrn04-test-project";
        const string repo = "lrn04-test-repo";
        await using var store = new PostgresTicketFixLinkStore(options);

        try
        {
            // Revert arrives before any ticket-fix association exists.
            Assert.Equal(0, await store.RecordRevertAsync(
                uniqueSource, project, repo, originalSha, revertingSha));
            Assert.Equal(0, await store.RecordRevertAsync(
                uniqueSource, project, repo, originalSha, revertingSha));

            var claim = new TicketFixClaim(
                uniqueSource, project, "ticket-1", repo, "pr-1", originalSha,
                TicketFixSourceClassification.Explicit, ["evidence:test-pr-1"], []);
            var key = await store.UpsertSourceAsync(claim);
            // An unrelated repository must not inherit the observed revert.
            var unrelated = claim with { RepositoryId = "other-repo", TicketId = "ticket-2" };
            var unrelatedKey = await store.UpsertSourceAsync(unrelated);
            // An unrelated commit in the same repository must not inherit it either.
            var otherCommit = claim with { CommitSha = unrelatedSha, TicketId = "ticket-3" };
            var otherCommitKey = await store.UpsertSourceAsync(otherCommit);

            await using var db = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
            {
                Host = options.Host, Port = options.Port, Database = options.Name,
                Username = options.User, Password = options.Password,
            }.ConnectionString);
            await db.OpenAsync();
            async Task<string> ReadReverts(string associationKey)
            {
                await using var cmd = new NpgsqlCommand(
                    "SELECT revert_commit_shas::text FROM evidence.ticket_fix_links WHERE association_key=@key", db);
                cmd.Parameters.AddWithValue("key", associationKey);
                return (string)(await cmd.ExecuteScalarAsync())!;
            }

            Assert.Contains(revertingSha, await ReadReverts(key));
            Assert.Equal("[]", await ReadReverts(unrelatedKey));
            Assert.Equal("[]", await ReadReverts(otherCommitKey));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.RecordReviewAsync(key, TicketFixReviewOutcome.Verified, "review-after-revert"));

            // Replaying the fix and reverting commit does not duplicate marker rows. Review R4-04:
            // RecordRevertAsync now reports the current, recomputed state; replaying the identical
            // revert changes nothing (already correctly set), so it reports 0 rows changed, not 1.
            Assert.Equal(key, await store.UpsertSourceAsync(claim));
            Assert.Equal(0, await store.RecordRevertAsync(
                uniqueSource, project, repo, originalSha, revertingSha));

            await using var check = new NpgsqlCommand(
                "SELECT count(*) FROM evidence.ticket_fix_revert_observations " +
                "WHERE source_instance=@source AND project_id=@project AND repository_id=@repo", db);
            check.Parameters.AddWithValue("source", uniqueSource);
            check.Parameters.AddWithValue("project", project);
            check.Parameters.AddWithValue("repo", repo);
            Assert.Equal(1L, (long)(await check.ExecuteScalarAsync())!);
            using var json = System.Text.Json.JsonDocument.Parse(await ReadReverts(key));
            Assert.Equal(revertingSha, Assert.Single(json.RootElement.EnumerateArray()).GetString());
        }
        finally
        {
            // Delete only uniquely-scoped test data, leaving all other associations untouched.
            await using var db = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
            {
                Host = options.Host, Port = options.Port, Database = options.Name,
                Username = options.User, Password = options.Password,
            }.ConnectionString);
            await db.OpenAsync();
            foreach (var table in new[] { "ticket_fix_reviews", "ticket_fix_links", "ticket_fix_revert_observations" })
            {
                var sql = table == "ticket_fix_reviews"
                    ? "DELETE FROM evidence.ticket_fix_reviews WHERE association_key IN " +
                      "(SELECT association_key FROM evidence.ticket_fix_links WHERE source_instance=@source)"
                    : "DELETE FROM evidence." + table + " WHERE source_instance=@source";
                await using var cmd = new NpgsqlCommand(sql, db);
                cmd.Parameters.AddWithValue("source", uniqueSource);
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    [DatabaseFact("VISIONIQ_TICKET_FIX_DB_INTEGRATION")]
    public async Task A_revert_of_a_revert_restores_the_original_fix_and_a_further_revert_hides_it_again()
    {
        // Review R4-04, against real PostgreSQL: a (the fix) is reverted by b; c reverts b, which
        // restores a; d reverts c, which hides a again -- a 3-level chain, not just a single pair.
        var password = Environment.GetEnvironmentVariable("DB_API_PASSWORD");
        Assert.False(string.IsNullOrWhiteSpace(password));
        var options = new DatabaseOptions(
            Environment.GetEnvironmentVariable("DB_HOST") ?? "127.0.0.1",
            int.TryParse(Environment.GetEnvironmentVariable("DB_PORT"), out var port) ? port : 5432,
            Environment.GetEnvironmentVariable("DB_NAME") ?? "visioniq",
            Environment.GetEnvironmentVariable("DB_API_USER") ?? "visioniq_api",
            password!, "disable");

        var uniqueSource = "lrn04-r404-" + Guid.NewGuid().ToString("N");
        const string project = "r404-test-project";
        const string repo = "r404-test-repo";
        var a = new string('a', 40);
        var b = new string('b', 40);
        var c = new string('c', 40);
        var d = new string('d', 40);
        await using var store = new PostgresTicketFixLinkStore(options);

        try
        {
            var claim = new TicketFixClaim(
                uniqueSource, project, "ticket-r404", repo, "pr-r404", a,
                TicketFixSourceClassification.Explicit, ["evidence:test-pr-r404"], []);
            var key = await store.UpsertSourceAsync(claim);

            await using var db = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
            {
                Host = options.Host, Port = options.Port, Database = options.Name,
                Username = options.User, Password = options.Password,
            }.ConnectionString);
            await db.OpenAsync();
            async Task<bool> IsReverted()
            {
                await using var cmd = new NpgsqlCommand(
                    "SELECT jsonb_array_length(revert_commit_shas) FROM evidence.ticket_fix_links WHERE association_key=@key",
                    db);
                cmd.Parameters.AddWithValue("key", key);
                return (int)(await cmd.ExecuteScalarAsync())! > 0;
            }

            Assert.False(await IsReverted());

            await store.RecordRevertAsync(uniqueSource, project, repo, a, b);
            Assert.True(await IsReverted(), "b reverts a: a should be hidden");

            await store.RecordRevertAsync(uniqueSource, project, repo, b, c);
            Assert.False(await IsReverted(), "c reverts b (the revert): a should be restored");

            // A previously blocked verification succeeds again now that a is no longer reverted.
            await store.RecordReviewAsync(key, TicketFixReviewOutcome.Verified, "review-after-restore");

            await store.RecordRevertAsync(uniqueSource, project, repo, c, d);
            Assert.True(await IsReverted(), "d reverts c (re-applying b's revert): a should be hidden again");
        }
        finally
        {
            await using var db = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
            {
                Host = options.Host, Port = options.Port, Database = options.Name,
                Username = options.User, Password = options.Password,
            }.ConnectionString);
            await db.OpenAsync();
            foreach (var table in new[] { "ticket_fix_reviews", "ticket_fix_links", "ticket_fix_revert_observations" })
            {
                var sql = table == "ticket_fix_reviews"
                    ? "DELETE FROM evidence.ticket_fix_reviews WHERE association_key IN " +
                      "(SELECT association_key FROM evidence.ticket_fix_links WHERE source_instance=@source)"
                    : "DELETE FROM evidence." + table + " WHERE source_instance=@source";
                await using var cmd = new NpgsqlCommand(sql, db);
                cmd.Parameters.AddWithValue("source", uniqueSource);
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    [DatabaseFact("VISIONIQ_TICKET_FIX_DB_INTEGRATION")]
    public async Task Reconcile_retracts_a_dropped_link_and_rejects_a_stale_replay()
    {
        // Review R4-02: end-to-end against real PostgreSQL. A source entity (a pull request) asserts
        // ticket 101, then a newer complete snapshot of the same entity drops it -- the association is
        // retracted (marked, never deleted). A revision older than the one already recorded then
        // replays the original claim and must NOT reactivate the retraction.
        var password = Environment.GetEnvironmentVariable("DB_API_PASSWORD");
        Assert.False(string.IsNullOrWhiteSpace(password));
        var options = new DatabaseOptions(
            Environment.GetEnvironmentVariable("DB_HOST") ?? "127.0.0.1",
            int.TryParse(Environment.GetEnvironmentVariable("DB_PORT"), out var port) ? port : 5432,
            Environment.GetEnvironmentVariable("DB_NAME") ?? "visioniq",
            Environment.GetEnvironmentVariable("DB_API_USER") ?? "visioniq_api",
            password!, "disable");

        var source = "test-source-" + Guid.NewGuid().ToString("N");
        var sourceItemKey = new string('7', 64);
        var ticket = Guid.NewGuid().ToString("N");
        var claim = new TicketFixClaim(
            source, "test-project", ticket, "repo-one", "pr-one", null,
            TicketFixSourceClassification.Explicit, ["ado:pr_source"], []);
        var key = claim.AssociationKey();

        await using var store = new PostgresTicketFixLinkStore(options);
        var t1 = DateTimeOffset.UtcNow.AddHours(-2);
        var t2 = DateTimeOffset.UtcNow.AddHours(-1);

        // Revision 1 asserts the link.
        await store.ReconcileSourceAsync(sourceItemKey, "rev1", t1, isCompleteSnapshot: true, [claim]);
        var afterFirst = await ReadRetractionAsync(options, key);
        Assert.Null(afterFirst.RetractedAt);

        // Revision 2 (newer, complete) no longer asserts it -- retracted.
        await store.ReconcileSourceAsync(sourceItemKey, "rev2", t2, isCompleteSnapshot: true, []);
        var afterSecond = await ReadRetractionAsync(options, key);
        Assert.NotNull(afterSecond.RetractedAt);
        Assert.Equal("missing_in_source", afterSecond.RetractionReason);
        Assert.Equal("reconciled", afterSecond.ProvenanceStatus);

        // Revision 1 replayed (older than rev2, already recorded) must not reactivate it.
        await store.ReconcileSourceAsync(sourceItemKey, "rev1-replay", t1, isCompleteSnapshot: true, [claim]);
        var afterReplay = await ReadRetractionAsync(options, key);
        Assert.NotNull(afterReplay.RetractedAt);

        // A genuine re-link (a newer revision than rev2) clears the retraction.
        await store.ReconcileSourceAsync(sourceItemKey, "rev3", DateTimeOffset.UtcNow, isCompleteSnapshot: true, [claim]);
        var afterRelink = await ReadRetractionAsync(options, key);
        Assert.Null(afterRelink.RetractedAt);

        await using var cleanup = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
        {
            Host = options.Host, Port = options.Port, Database = options.Name,
            Username = options.User, Password = options.Password,
        }.ConnectionString);
        await cleanup.OpenAsync();
        await using (var deleteAssoc = new NpgsqlCommand(
            "DELETE FROM evidence.ticket_fix_link_source_associations WHERE source_item_key = @item", cleanup))
        {
            deleteAssoc.Parameters.AddWithValue("item", sourceItemKey);
            await deleteAssoc.ExecuteNonQueryAsync();
        }

        await using (var deleteSource = new NpgsqlCommand(
            "DELETE FROM evidence.ticket_fix_link_sources WHERE source_item_key = @item", cleanup))
        {
            deleteSource.Parameters.AddWithValue("item", sourceItemKey);
            await deleteSource.ExecuteNonQueryAsync();
        }

        // Finding #4 (second-pass correction): the append-only lifecycle log references
        // ticket_fix_links with ON DELETE RESTRICT, so its rows for this test's association must be
        // cleared before the link row itself can be deleted below.
        await using (var deleteEvents = new NpgsqlCommand(
            "DELETE FROM evidence.ticket_fix_link_lifecycle_events WHERE association_key = @key", cleanup))
        {
            deleteEvents.Parameters.AddWithValue("key", key);
            await deleteEvents.ExecuteNonQueryAsync();
        }

        await using (var deleteLink = new NpgsqlCommand(
            "DELETE FROM evidence.ticket_fix_links WHERE source_instance = @source", cleanup))
        {
            deleteLink.Parameters.AddWithValue("source", source);
            await deleteLink.ExecuteNonQueryAsync();
        }
    }

    [DatabaseFact("VISIONIQ_TICKET_FIX_DB_INTEGRATION")]
    public async Task An_active_prs_recheck_is_accepted_when_content_changed_even_at_the_same_timestamp()
    {
        // Second-pass correction, Finding #1: an active (not-yet-completed) pull request's
        // LastActivity can stay exactly the same across two sync passes while its description or
        // linked work items genuinely change -- R1-06 re-checks active PRs on every sync regardless
        // of cursor state specifically to catch this. The original strict `<` comparison on
        // source_revision_time permanently rejected every such re-check after the first acceptance,
        // because the timestamp never advances. Also covers the negative and out-of-order cases the
        // reviewer explicitly asked for: an identical replay at the same timestamp must still be
        // rejected, and a revision genuinely older than the one already recorded must be rejected too.
        var options = Settings();
        var sourceItemKey = new string('8', 64);
        var ticketA = Guid.NewGuid().ToString("N");
        var ticketB = Guid.NewGuid().ToString("N");
        var source = "test-source-" + Guid.NewGuid().ToString("N");
        var sameInstant = DateTimeOffset.UtcNow.AddHours(-3);

        // claimA and claimB differ only in which ticket the still-open PR links to -- exactly what
        // changes on an active PR whose LastActivity never moves (its linked work items are edited,
        // not its commits), and what distinguishes them as two different associations.
        var claimA = new TicketFixClaim(
            source, "test-project", ticketA, "repo-one", "pr-active", null,
            TicketFixSourceClassification.Explicit, ["ado:pr_source"], []);
        var claimB = claimA with { TicketId = ticketB };
        var keyA = claimA.AssociationKey();
        var keyB = claimB.AssociationKey();

        await using var store = new PostgresTicketFixLinkStore(options);
        try
        {
            // rev1: first observation of the active PR, asserting claim A.
            await store.ReconcileSourceAsync(sourceItemKey, "rev1", sameInstant, isCompleteSnapshot: true, [claimA]);
            Assert.Null((await ReadRetractionAsync(options, keyA)).RetractedAt);

            // rev2: same instant (LastActivity unchanged), but the PR's real content changed -- a
            // distinct revision id -- so claim A is dropped in favor of claim B. Must be ACCEPTED.
            await store.ReconcileSourceAsync(sourceItemKey, "rev2", sameInstant, isCompleteSnapshot: true, [claimB]);
            Assert.NotNull((await ReadRetractionAsync(options, keyA)).RetractedAt);
            Assert.Null((await ReadRetractionAsync(options, keyB)).RetractedAt);

            // Negative case: an identical replay of rev2 (same id, same time) must be REJECTED, not
            // just accepted-and-idempotent -- it must not re-run retraction/assertion logic at all.
            await store.ReconcileSourceAsync(sourceItemKey, "rev2", sameInstant, isCompleteSnapshot: true, [claimA]);
            Assert.NotNull((await ReadRetractionAsync(options, keyA)).RetractedAt);

            // Out-of-order case: a revision strictly older than the one already recorded must be
            // REJECTED even though it asserts claim A again.
            await store.ReconcileSourceAsync(
                sourceItemKey, "rev0-late-arrival", sameInstant.AddMinutes(-5), isCompleteSnapshot: true, [claimA]);
            Assert.NotNull((await ReadRetractionAsync(options, keyA)).RetractedAt);
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
            {
                Host = options.Host, Port = options.Port, Database = options.Name,
                Username = options.User, Password = options.Password,
            }.ConnectionString);
            await cleanup.OpenAsync();
            await using (var deleteAssoc = new NpgsqlCommand(
                "DELETE FROM evidence.ticket_fix_link_source_associations WHERE source_item_key = @item", cleanup))
            {
                deleteAssoc.Parameters.AddWithValue("item", sourceItemKey);
                await deleteAssoc.ExecuteNonQueryAsync();
            }
            await using (var deleteSource = new NpgsqlCommand(
                "DELETE FROM evidence.ticket_fix_link_sources WHERE source_item_key = @item", cleanup))
            {
                deleteSource.Parameters.AddWithValue("item", sourceItemKey);
                await deleteSource.ExecuteNonQueryAsync();
            }
            await using (var deleteEvents = new NpgsqlCommand(
                "DELETE FROM evidence.ticket_fix_link_lifecycle_events WHERE association_key = ANY(@keys)", cleanup))
            {
                deleteEvents.Parameters.AddWithValue("keys", new[] { keyA, keyB });
                await deleteEvents.ExecuteNonQueryAsync();
            }
            await using (var deleteLink = new NpgsqlCommand(
                "DELETE FROM evidence.ticket_fix_links WHERE source_instance = @source", cleanup))
            {
                deleteLink.Parameters.AddWithValue("source", source);
                await deleteLink.ExecuteNonQueryAsync();
            }
        }
    }

    [DatabaseFact("VISIONIQ_TICKET_FIX_DB_INTEGRATION")]
    public async Task Repeated_retract_and_relink_cycles_leave_a_full_lifecycle_history()
    {
        // Second-pass correction, Finding #4: ticket_fix_links.retracted_at and
        // ticket_fix_link_source_associations.ended_at each only show the CURRENT state of a
        // transition. A link that is retracted, relinked, then retracted again a second time must
        // not lose the record that the first retraction ever happened -- every transition is
        // expected to append to evidence.ticket_fix_link_lifecycle_events, never overwrite it.
        var options = Settings();
        var sourceItemKey = new string('9', 64);
        var ticket = Guid.NewGuid().ToString("N");
        var source = "test-source-" + Guid.NewGuid().ToString("N");
        var t0 = DateTimeOffset.UtcNow.AddHours(-4);

        var claim = new TicketFixClaim(
            source, "test-project", ticket, "repo-one", "pr-cycle", null,
            TicketFixSourceClassification.Explicit, ["ado:pr_source"], []);
        var key = claim.AssociationKey();

        await using var store = new PostgresTicketFixLinkStore(options);
        try
        {
            await store.ReconcileSourceAsync(sourceItemKey, "rev1", t0, isCompleteSnapshot: true, [claim]);
            // Cycle 1: retract, then relink.
            await store.ReconcileSourceAsync(sourceItemKey, "rev2", t0.AddMinutes(1), isCompleteSnapshot: true, []);
            await store.ReconcileSourceAsync(sourceItemKey, "rev3", t0.AddMinutes(2), isCompleteSnapshot: true, [claim]);
            // Cycle 2: retract again.
            await store.ReconcileSourceAsync(sourceItemKey, "rev4", t0.AddMinutes(3), isCompleteSnapshot: true, []);

            // Current state only shows the latest retraction -- confirming the test setup, not the fix.
            Assert.NotNull((await ReadRetractionAsync(options, key)).RetractedAt);

            await using var db = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
            {
                Host = options.Host, Port = options.Port, Database = options.Name,
                Username = options.User, Password = options.Password,
            }.ConnectionString);
            await db.OpenAsync();
            await using var query = new NpgsqlCommand("""
                SELECT event_type, reason FROM evidence.ticket_fix_link_lifecycle_events
                WHERE association_key = @key ORDER BY occurred_at, id
                """, db);
            query.Parameters.AddWithValue("key", key);
            await using var reader = await query.ExecuteReaderAsync();
            var events = new List<(string EventType, string? Reason)>();
            while (await reader.ReadAsync())
            {
                events.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            }

            // The full cycle survives: retracted (cycle 1) -> cleared (relink) -> retracted (cycle 2).
            // If the lifecycle were current-state-only, the middle "cleared" transition would be lost.
            Assert.Equal(3, events.Count);
            Assert.Equal(("retracted", "missing_in_source"), events[0]);
            Assert.Equal(("cleared", (string?)null), events[1]);
            Assert.Equal(("retracted", "missing_in_source"), events[2]);
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
            {
                Host = options.Host, Port = options.Port, Database = options.Name,
                Username = options.User, Password = options.Password,
            }.ConnectionString);
            await cleanup.OpenAsync();
            await using (var deleteAssoc = new NpgsqlCommand(
                "DELETE FROM evidence.ticket_fix_link_source_associations WHERE source_item_key = @item", cleanup))
            {
                deleteAssoc.Parameters.AddWithValue("item", sourceItemKey);
                await deleteAssoc.ExecuteNonQueryAsync();
            }
            await using (var deleteSource = new NpgsqlCommand(
                "DELETE FROM evidence.ticket_fix_link_sources WHERE source_item_key = @item", cleanup))
            {
                deleteSource.Parameters.AddWithValue("item", sourceItemKey);
                await deleteSource.ExecuteNonQueryAsync();
            }
            await using (var deleteEvents = new NpgsqlCommand(
                "DELETE FROM evidence.ticket_fix_link_lifecycle_events WHERE association_key = @key", cleanup))
            {
                deleteEvents.Parameters.AddWithValue("key", key);
                await deleteEvents.ExecuteNonQueryAsync();
            }
            await using (var deleteLink = new NpgsqlCommand(
                "DELETE FROM evidence.ticket_fix_links WHERE source_instance = @source", cleanup))
            {
                deleteLink.Parameters.AddWithValue("source", source);
                await deleteLink.ExecuteNonQueryAsync();
            }
        }
    }

    private static DatabaseOptions Settings()
    {
        var password = Environment.GetEnvironmentVariable("DB_API_PASSWORD");
        Assert.False(string.IsNullOrWhiteSpace(password));
        return new DatabaseOptions(
            Environment.GetEnvironmentVariable("DB_HOST") ?? "127.0.0.1",
            int.TryParse(Environment.GetEnvironmentVariable("DB_PORT"), out var port) ? port : 5432,
            Environment.GetEnvironmentVariable("DB_NAME") ?? "visioniq",
            Environment.GetEnvironmentVariable("DB_API_USER") ?? "visioniq_api",
            password!, "disable");
    }

    private static async Task<(DateTimeOffset? RetractedAt, string? RetractionReason, string ProvenanceStatus)>
        ReadRetractionAsync(DatabaseOptions options, string associationKey)
    {
        await using var db = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
        {
            Host = options.Host, Port = options.Port, Database = options.Name,
            Username = options.User, Password = options.Password,
        }.ConnectionString);
        await db.OpenAsync();
        await using var query = new NpgsqlCommand(
            "SELECT retracted_at, retraction_reason, provenance_status FROM evidence.ticket_fix_links WHERE association_key = @key",
            db);
        query.Parameters.AddWithValue("key", associationKey);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2));
    }

    [Fact]
    public void Scoped_association_identity_is_not_based_on_display_titles_or_timestamps()
    {
        var claim = new TicketFixClaim(
            "source-a", "project-a", "101", "repo", "42", "commit",
            TicketFixSourceClassification.Explicit, ["ado:pr"], []);
        var key = claim.AssociationKey();
        Assert.Equal(64, key.Length);
        Assert.NotEqual(key, (claim with { ProjectId = "project-b" }).AssociationKey());
        Assert.NotEqual(key, (claim with { RepositoryId = "repo-other" }).AssociationKey());
        Assert.Throws<ArgumentException>(() =>
            (claim with { SourceEvidenceRefs = [] }).AssociationKey());
    }
}
