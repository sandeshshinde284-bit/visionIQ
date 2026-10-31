using Npgsql;
using VisionIQ.Api.Configuration;
using VisionIQ.Api.Features.Evidence;
using VisionIQ.Api.Features.Graph;
using VisionIQ.Api.Features.TicketFixes;

namespace VisionIQ.Api.Tests;

/// <summary>
/// LRN-07 disposable PostgreSQL integration tests for immutable graph history,
/// LRN-04 dispute suppression and exact-revision ACL enforcement.
/// </summary>
public sealed class GraphDatabaseIntegrationTests
{
    /// <summary>Creates a test-only restricted database configuration from CI values.</summary>
    private static DatabaseOptions Settings()
    {
        var password = Environment.GetEnvironmentVariable("DB_API_PASSWORD");
        Assert.False(string.IsNullOrWhiteSpace(password));
        return new DatabaseOptions(
            Environment.GetEnvironmentVariable("DB_HOST") ?? "127.0.0.1",
            5432,
            Environment.GetEnvironmentVariable("DB_NAME") ?? "visioniq",
            Environment.GetEnvironmentVariable("DB_API_USER") ?? "visioniq_api",
            password!, "disable");
    }

    /// <summary>Constructs the restricted-role connection string for the disposable database.</summary>
    private static string ConnectionString(DatabaseOptions options) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = options.Host, Port = options.Port, Database = options.Name,
            Username = options.User, Password = options.Password,
        }.ConnectionString;

    /// <summary>
    /// Writes test-only immutable source revisions, preserving the actual LRN-03 FK
    /// and avoiding direct schema changes or migration-account credentials.
    /// </summary>
    private static async Task AddEvidenceAsync(
        NpgsqlConnection db, EvidenceIdentity identity, string revision,
        DateTimeOffset sourceTime)
    {
        var key = identity.ItemKey();
        await using var item = new NpgsqlCommand("""
            INSERT INTO evidence.source_items (
                item_key, provider, source_instance, project_id, repository_id,
                kind, external_id, first_observed_at, last_observed_at
            ) VALUES (@key, @provider, @source, @project, @repo,
                      @kind, @id, @time, @time)
            ON CONFLICT (item_key) DO NOTHING
            """, db);
        item.Parameters.AddWithValue("key", key);
        item.Parameters.AddWithValue("provider", identity.Provider);
        item.Parameters.AddWithValue("source", identity.SourceInstance);
        item.Parameters.AddWithValue("project", identity.ProjectId);
        item.Parameters.AddWithValue("repo", (object?)identity.RepositoryId ?? DBNull.Value);
        item.Parameters.AddWithValue("kind", identity.Kind);
        item.Parameters.AddWithValue("id", identity.ExternalId);
        item.Parameters.AddWithValue("time", sourceTime);
        await item.ExecuteNonQueryAsync();

        await using var proof = new NpgsqlCommand("""
            INSERT INTO evidence.source_revisions (
                item_key, provider_revision_id, source_updated_at, observed_at,
                content_sha256, source_url, retrieved_under_principal,
                acl_verified, acl_principals, redacted_payload, redaction_version,
                normalizer_version, raw_snapshot_uri
            ) VALUES (@key, @revision, @time, @time, @hash,
                      @url, 'test-source', TRUE, '["approved-group"]'::jsonb,
                      '{}'::jsonb, 'test-redacted', 'test-v1', @archive)
            ON CONFLICT (item_key, provider_revision_id) DO NOTHING
            """, db);
        proof.Parameters.AddWithValue("key", key);
        proof.Parameters.AddWithValue("revision", revision);
        proof.Parameters.AddWithValue("time", sourceTime);
        proof.Parameters.AddWithValue("hash", GraphHashes.Hash(key, revision));
        proof.Parameters.AddWithValue("url", "https://dev.azure.com/test/" + identity.ProjectId);
        proof.Parameters.AddWithValue("archive", "gs://disposable-ci-only/" + key);
        await proof.ExecuteNonQueryAsync();

        await using var latest = new NpgsqlCommand("""
            UPDATE evidence.source_items
            SET latest_revision_id = @revision, latest_source_updated_at = @time,
                last_observed_at = @time
            WHERE item_key = @key
              AND (latest_source_updated_at IS NULL OR latest_source_updated_at <= @time)
            """, db);
        latest.Parameters.AddWithValue("key", key);
        latest.Parameters.AddWithValue("revision", revision);
        latest.Parameters.AddWithValue("time", sourceTime);
        await latest.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Immutable_edge_replay_and_newer_retraction_are_correct()
    {
        if (Environment.GetEnvironmentVariable("VISIONIQ_GRAPH_DB_INTEGRATION") != "1")
        {
            return;
        }

        var settings = Settings();
        var project = "test-" + Guid.NewGuid().ToString("N");
        var instance = "https://dev.azure.com/visioniq-ci";
        var ticket = new EvidenceIdentity("azure_devops", instance, project, null,
            "work_item_current", "123");
        var pr = new EvidenceIdentity("azure_devops", instance, project, "repo",
            "pull_request", "42");
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        await using (var db = new NpgsqlConnection(ConnectionString(settings)))
        {
            await db.OpenAsync();
            await AddEvidenceAsync(db, ticket, "7", now);
            await AddEvidenceAsync(db, pr, "1", now);
            await AddEvidenceAsync(db, pr, "2", now.AddMinutes(1));
        }

        var from = new GraphNode(ticket.ItemKey(), GraphNodeKind.WorkItem, "123");
        var to = new GraphNode(pr.ItemKey(), GraphNodeKind.PullRequest, "42");
        var first = new GraphAssertion(from, to, GraphRelationKind.TicketPr,
            pr.ItemKey(), "1", GraphClassification.Explicit, GraphAssertionState.Active, now);
        await using var store = new PostgresRelationshipGraphStore(settings);
        await store.SaveAsync(first);
        await store.SaveAsync(first);
        Assert.Single(await store.OutgoingAsync(from.NodeKey(), 20));
        Assert.Equal(from, await store.GetNodeAsync(from.NodeKey()));

        var retracted = first with
        {
            ProvenanceRevision = "2",
            State = GraphAssertionState.Retracted,
            SourceAssertedAt = now.AddMinutes(1),
        };
        await store.SaveAsync(retracted);
        Assert.Empty(await store.OutgoingAsync(from.NodeKey(), 20));

        await using var connection = new NpgsqlConnection(ConnectionString(settings));
        await connection.OpenAsync();
        await using var check = new NpgsqlCommand("""
            SELECT count(*) FROM evidence.graph_assertions
            WHERE relationship_key = @relation
            """, connection);
        check.Parameters.AddWithValue("relation", first.RelationshipKey());
        Assert.Equal(2L, (long)(await check.ExecuteScalarAsync())!);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveAsync(first with { Classification = GraphClassification.Verified }));
    }

    [Fact]
    public async Task Disputed_ticket_fix_and_denied_historical_acl_are_not_traversed()
    {
        if (Environment.GetEnvironmentVariable("VISIONIQ_GRAPH_DB_INTEGRATION") != "1")
        {
            return;
        }

        var settings = Settings();
        var project = "test-" + Guid.NewGuid().ToString("N");
        var instance = "https://dev.azure.com/visioniq-ci";
        var ticket = new EvidenceIdentity("azure_devops", instance, project, null,
            "work_item_current", "123");
        var pr = new EvidenceIdentity("azure_devops", instance, project, "repo",
            "pull_request", "42");
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        await using (var db = new NpgsqlConnection(ConnectionString(settings)))
        {
            await db.OpenAsync();
            await AddEvidenceAsync(db, ticket, "7", now);
            await AddEvidenceAsync(db, pr, "1", now);
        }

        var claim = new TicketFixClaim(
            instance, project, "123", "repo", "42", null,
            TicketFixSourceClassification.Explicit,
            ["evidence:" + pr.ItemKey() + ":1"], []);
        await using var fixes = new PostgresTicketFixLinkStore(settings);
        var association = await fixes.UpsertSourceAsync(claim);
        var from = new GraphNode(ticket.ItemKey(), GraphNodeKind.WorkItem, "123");
        var to = new GraphNode(pr.ItemKey(), GraphNodeKind.PullRequest, "42");
        var edge = new GraphAssertion(from, to, GraphRelationKind.TicketPr,
            pr.ItemKey(), "1", GraphClassification.Explicit,
            GraphAssertionState.Active, now, association);
        await using var graph = new PostgresRelationshipGraphStore(settings);
        await graph.SaveAsync(edge);
        Assert.Single(await graph.OutgoingAsync(from.NodeKey(), 10));

        await using var acl = new PostgresGraphRevisionAccess(settings);
        var requester = new EvidenceRequester("reviewer", new HashSet<string> { "approved-group" });
        Assert.True(await acl.CanReadRevisionAsync(pr.ItemKey(), "1", requester));
        Assert.False(await acl.CanReadRevisionAsync(pr.ItemKey(), "missing", requester));
        Assert.False(await acl.CanReadRevisionAsync(pr.ItemKey(), "1",
            new EvidenceRequester("unknown", new HashSet<string> { "unauthorized" })));

        await fixes.RecordReviewAsync(association, TicketFixReviewOutcome.Disputed,
            "review-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(await graph.OutgoingAsync(from.NodeKey(), 10));
    }
}
