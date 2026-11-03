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

            // Replaying the fix and reverting commit does not duplicate marker rows.
            Assert.Equal(key, await store.UpsertSourceAsync(claim));
            Assert.Equal(1, await store.RecordRevertAsync(
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
