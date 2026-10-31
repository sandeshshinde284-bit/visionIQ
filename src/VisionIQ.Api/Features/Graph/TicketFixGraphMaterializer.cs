using System.Text.Json;
using Npgsql;
using VisionIQ.Api.Configuration;

namespace VisionIQ.Api.Features.Graph;

/// <summary>
/// Rebuildable LRN-04 -> LRN-07 projection. Resolves relationships only where
/// both endpoints have already been archived as canonical evidence. Re-running
/// after late-arriving source history fills earlier gaps without duplicates.
/// </summary>
public sealed class TicketFixGraphMaterializer : IAsyncDisposable
{
    private readonly NpgsqlDataSource source;
    private readonly IRelationshipGraphStore graph;

    /// <summary>Accept read-only source lookups and a separately validated graph writer.</summary>
    public TicketFixGraphMaterializer(DatabaseOptions options, IRelationshipGraphStore graph)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.graph = graph ?? throw new ArgumentNullException(nameof(graph));
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = options.Host, Port = options.Port, Database = options.Name,
            Username = options.User, Password = options.Password, Timeout = 10,
        };
        builder["SSL Mode"] = options.SslMode;
        source = NpgsqlDataSource.Create(builder.ConnectionString);
    }

    /// <summary>
    /// Project explicit source links for a single approved ADO organization.
    /// Disputed or reverted associations remain stored, but graph traversal
    /// checks their live LRN-04 verdict before returning the relationship.
    /// </summary>
    public async Task<int> ReconcileAsync(
        string sourceInstance, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstance);
        var completed = 0;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT association_key, project_id, ticket_id, repository_id,
                   pull_request_id, commit_sha, source_evidence_refs::text
            FROM evidence.ticket_fix_links
            WHERE source_instance = @source AND source_classification = 'explicit'
            ORDER BY association_key
            """, connection);
        command.Parameters.AddWithValue("source", sourceInstance);
        await using var rows = await command.ExecuteReaderAsync(cancellationToken);
        while (await rows.ReadAsync(cancellationToken))
        {
            var associationKey = rows.GetString(0);
            var project = rows.GetString(1);
            var ticketId = rows.GetString(2);
            var repo = rows.IsDBNull(3) ? null : rows.GetString(3);
            var prId = rows.IsDBNull(4) ? null : rows.GetString(4);
            var sha = rows.IsDBNull(5) ? null : rows.GetString(5);
            var refs = JsonSerializer.Deserialize<string[]>(rows.GetString(6)) ?? [];

            // Resolve by exact source identity. A ticket-only artifact reference
            // may lack repository scope: never guess its PR/commit repository.
            if (repo is null)
            {
                continue;
            }

            var ticket = await FindCanonicalAsync(sourceInstance, project, null,
                "work_item_current", ticketId, cancellationToken)
                ?? await FindCanonicalAsync(sourceInstance, project, null,
                    "work_item", ticketId, cancellationToken);
            var pr = prId is null ? null
                : await FindCanonicalAsync(sourceInstance, project, repo,
                    "pull_request", prId, cancellationToken);
            var commit = sha is null ? null
                : await FindCanonicalAsync(sourceInstance, project, repo,
                    "commit", sha, cancellationToken);
            if (ticket is null)
            {
                continue;
            }

            foreach (var evidenceRef in refs.Distinct(StringComparer.Ordinal))
            {
                if (!TryParseReference(evidenceRef, out var itemKey, out var revision))
                {
                    continue;
                }

                var assertedAt = await FindRevisionTimeAsync(itemKey, revision, cancellationToken);
                if (assertedAt is null)
                {
                    continue;
                }

                var ticketNode = new GraphNode(ticket, GraphNodeKind.WorkItem, ticketId);
                var prNode = pr is null ? null : new GraphNode(pr, GraphNodeKind.PullRequest, prId!);
                var commitNode = commit is null ? null
                    : new GraphNode(commit, GraphNodeKind.Commit, sha!);

                if (prNode is not null)
                {
                    completed += await PersistAsync(new GraphAssertion(
                        ticketNode, prNode, GraphRelationKind.TicketPr,
                        itemKey, revision, GraphClassification.Explicit,
                        GraphAssertionState.Active, assertedAt.Value, associationKey),
                        cancellationToken);
                }

                if (prNode is not null && commitNode is not null)
                {
                    completed += await PersistAsync(new GraphAssertion(
                        prNode, commitNode, GraphRelationKind.PrCommit,
                        itemKey, revision, GraphClassification.Explicit,
                        GraphAssertionState.Active, assertedAt.Value, associationKey),
                        cancellationToken);
                }
                else if (prNode is null && commitNode is not null)
                {
                    completed += await PersistAsync(new GraphAssertion(
                        ticketNode, commitNode, GraphRelationKind.TicketCommit,
                        itemKey, revision, GraphClassification.Explicit,
                        GraphAssertionState.Active, assertedAt.Value, associationKey),
                        cancellationToken);
                }
            }
        }

        return completed;
    }

    /// <summary>Write a revision-specific assertion; idempotent replays are safe.</summary>
    private async Task<int> PersistAsync(
        GraphAssertion assertion, CancellationToken cancellationToken)
    {
        await graph.SaveAsync(assertion, cancellationToken);
        return 1;
    }

    /// <summary>Resolve only exact, canonical provider-native node identities.</summary>
    private async Task<string?> FindCanonicalAsync(
        string instance, string project, string? repo, string kind, string id,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT item_key FROM evidence.source_items
            WHERE provider = 'azure_devops' AND source_instance = @source
              AND project_id = @project AND repository_id IS NOT DISTINCT FROM @repo
              AND kind = @kind AND external_id = @id
              AND latest_revision_id IS NOT NULL
            """, connection);
        command.Parameters.AddWithValue("source", instance);
        command.Parameters.AddWithValue("project", project);
        command.Parameters.AddWithValue("repo", (object?)repo ?? DBNull.Value);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("id", id);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>Read the trusted source revision timestamp, not the ingestion clock.</summary>
    private async Task<DateTimeOffset?> FindRevisionTimeAsync(
        string itemKey, string revision, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT source_updated_at FROM evidence.source_revisions
            WHERE item_key = @key AND provider_revision_id = @revision
            """, connection);
        command.Parameters.AddWithValue("key", itemKey);
        command.Parameters.AddWithValue("revision", revision);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is DateTimeOffset time ? time : null;
    }

    /// <summary>Parse canonical source evidence references without splitting ISO timestamps.</summary>
    private static bool TryParseReference(
        string value, out string itemKey, out string revision)
    {
        itemKey = revision = string.Empty;
        if (!value.StartsWith("evidence:", StringComparison.Ordinal) ||
            value.Length < 9 + 64 + 2 || value[73] != ':')
        {
            return false;
        }

        itemKey = value.Substring(9, 64);
        revision = value[74..];
        return itemKey.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
               !string.IsNullOrWhiteSpace(revision) && revision.Length <= 512;
    }

    /// <summary>Dispose the pooled canonical source evidence reader.</summary>
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
