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
    [Fact]
    public async Task Review_history_is_immutable_and_source_replay_does_not_reset_verdict()
    {
        if (Environment.GetEnvironmentVariable("VISIONIQ_TICKET_FIX_DB_INTEGRATION") != "1")
        {
            return;
        }

        var password = Environment.GetEnvironmentVariable("DB_API_PASSWORD");
        Assert.False(string.IsNullOrWhiteSpace(password));
        var options = new DatabaseOptions(
            Environment.GetEnvironmentVariable("DB_HOST") ?? "127.0.0.1",
            5432,
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
