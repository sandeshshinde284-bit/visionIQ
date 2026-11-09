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
            int.TryParse(Environment.GetEnvironmentVariable("DB_PORT"), out var port) ? port : 5432,
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
        item.Parameters.Add(new Npgsql.NpgsqlParameter("repo", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = (object?)identity.RepositoryId ?? DBNull.Value,
        });
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

    [DatabaseFact("VISIONIQ_GRAPH_DB_INTEGRATION")]
    public async Task Immutable_edge_replay_and_newer_retraction_are_correct()
    {
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

    [DatabaseFact("VISIONIQ_GRAPH_DB_INTEGRATION")]
    public async Task Disputed_ticket_fix_and_denied_historical_acl_are_not_traversed()
    {
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

        await fixes.RecordReviewAsync(association, TicketFixReviewOutcome.Disputed,
            "review-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(await graph.OutgoingAsync(from.NodeKey(), 10));
    }

    [DatabaseFact("VISIONIQ_GRAPH_DB_INTEGRATION")]
    public async Task Reverting_one_commit_of_a_pull_request_hides_the_whole_pull_request_as_a_fix()
    {
        // Review R4-01: reproduces real ADO data (a pull request merged as two commits, a later
        // commit reverts the merge commit; the ticket_pr edge's provenance is the OTHER commit's
        // row, which was never itself marked reverted, so it used to stay visible).
        var settings = Settings();
        var project = "test-" + Guid.NewGuid().ToString("N");
        var instance = "https://dev.azure.com/visioniq-ci";
        var ticket = new EvidenceIdentity("azure_devops", instance, project, null,
            "work_item_current", "123");
        var pr = new EvidenceIdentity("azure_devops", instance, project, "repo",
            "pull_request", "812");
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        await using (var db = new NpgsqlConnection(ConnectionString(settings)))
        {
            await db.OpenAsync();
            await AddEvidenceAsync(db, ticket, "7", now);
            await AddEvidenceAsync(db, pr, "1", now);
        }

        await using var fixes = new PostgresTicketFixLinkStore(settings);
        var c1Sha = new string('1', 40);
        var mergeSha = new string('2', 40);
        var c1 = await fixes.UpsertSourceAsync(new TicketFixClaim(
            instance, project, "123", "repo", "812", c1Sha,
            TicketFixSourceClassification.Explicit, ["evidence:" + pr.ItemKey() + ":1"], []));
        await fixes.UpsertSourceAsync(new TicketFixClaim(
            instance, project, "123", "repo", "812", mergeSha,
            TicketFixSourceClassification.Explicit, ["evidence:" + pr.ItemKey() + ":1"], []));

        var from = new GraphNode(ticket.ItemKey(), GraphNodeKind.WorkItem, "123");
        var to = new GraphNode(pr.ItemKey(), GraphNodeKind.PullRequest, "812");
        // The materialized edge's provenance is c1's association row, which is never reverted.
        var edge = new GraphAssertion(from, to, GraphRelationKind.TicketPr,
            pr.ItemKey(), "1", GraphClassification.Explicit, GraphAssertionState.Active, now, c1);
        await using var graph = new PostgresRelationshipGraphStore(settings);
        await graph.SaveAsync(edge);
        Assert.Single(await graph.OutgoingAsync(from.NodeKey(), 10));

        // The revert targets the merge commit's row, a sibling of the one the edge cites.
        await fixes.RecordRevertAsync(instance, project, "repo", mergeSha, new string('3', 40));

        Assert.Empty(await graph.OutgoingAsync(from.NodeKey(), 10));
    }

    [DatabaseFact("VISIONIQ_GRAPH_DB_INTEGRATION")]
    public async Task Retracting_a_supersede_commit_does_not_hide_its_valid_replacement_sibling()
    {
        // Second-pass correction, Finding #2: a pull request's commit list changes from c1 to c2
        // across two revisions (a force-push or an amended commit, not a revert). c1's association is
        // retracted via normal ReconcileSourceAsync reconciliation -- never via RecordRevertAsync --
        // because the source stopped asserting it, not because anyone reverted anything. c2's
        // association is the valid, currently-asserted replacement for the exact same ticket/PR pair
        // and must stay visible: including sibling.retracted_at in the sibling-suppression OR-clause
        // previously hid it too, confusing "superseded by a newer revision" with "reverted".
        var settings = Settings();
        var project = "test-" + Guid.NewGuid().ToString("N");
        var instance = "https://dev.azure.com/visioniq-ci";
        var ticket = new EvidenceIdentity("azure_devops", instance, project, null,
            "work_item_current", "123");
        var pr = new EvidenceIdentity("azure_devops", instance, project, "repo",
            "pull_request", "901");
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        await using (var db = new NpgsqlConnection(ConnectionString(settings)))
        {
            await db.OpenAsync();
            await AddEvidenceAsync(db, ticket, "7", now);
            await AddEvidenceAsync(db, pr, "1", now);
            await AddEvidenceAsync(db, pr, "2", now.AddMinutes(1));
        }

        var sourceItemKey = pr.ItemKey();
        var c1Sha = new string('1', 40);
        var c2Sha = new string('2', 40);
        var c1Claim = new TicketFixClaim(
            instance, project, "123", "repo", "901", c1Sha,
            TicketFixSourceClassification.Explicit, ["evidence:" + sourceItemKey + ":1"], []);
        var c2Claim = c1Claim with
        {
            CommitSha = c2Sha, SourceEvidenceRefs = ["evidence:" + sourceItemKey + ":2"],
        };
        var key1 = c1Claim.AssociationKey();
        var key2 = c2Claim.AssociationKey();

        await using var fixes = new PostgresTicketFixLinkStore(settings);
        // Revision 1: PR asserts c1 only.
        await fixes.ReconcileSourceAsync(sourceItemKey, "rev1", now, isCompleteSnapshot: true, [c1Claim]);
        // Revision 2 (newer, complete): PR now asserts c2 instead -- c1 is retracted as superseded,
        // never reverted.
        await fixes.ReconcileSourceAsync(sourceItemKey, "rev2", now.AddMinutes(1), isCompleteSnapshot: true, [c2Claim]);

        await using (var db = new NpgsqlConnection(ConnectionString(settings)))
        {
            await db.OpenAsync();
            await using var check = new NpgsqlCommand(
                "SELECT retracted_at IS NOT NULL FROM evidence.ticket_fix_links WHERE association_key = @key", db);
            check.Parameters.AddWithValue("key", key1);
            Assert.True((bool)(await check.ExecuteScalarAsync())!, "c1 should be retracted as superseded");
        }

        var from = new GraphNode(ticket.ItemKey(), GraphNodeKind.WorkItem, "123");
        var to = new GraphNode(pr.ItemKey(), GraphNodeKind.PullRequest, "901");
        // The materialized edge's provenance is c2's (the current, valid) association.
        var edge = new GraphAssertion(from, to, GraphRelationKind.TicketPr,
            pr.ItemKey(), "2", GraphClassification.Explicit, GraphAssertionState.Active,
            now.AddMinutes(1), key2);
        await using var graph = new PostgresRelationshipGraphStore(settings);
        await graph.SaveAsync(edge);

        Assert.Single(await graph.OutgoingAsync(from.NodeKey(), 10));
    }
}
