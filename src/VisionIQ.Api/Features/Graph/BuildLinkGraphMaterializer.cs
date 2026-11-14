using Npgsql;
using VisionIQ.Api.Configuration;

namespace VisionIQ.Api.Features.Graph;

/// <summary>
/// LRN-07: connects CI evidence to code. <c>ci_commit</c>: a build built a commit (same repository, exact SHA).
/// <c>ci_pull_request</c>: a pull request triggered the build (PR builds compile a temporary merge commit that is
/// not on the branch, so this is what ties them to the fix). A build result is not a test result.
/// </summary>
public sealed class BuildLinkGraphMaterializer : IAsyncDisposable
{
    private readonly NpgsqlDataSource source;
    private readonly IRelationshipGraphStore graph;
    private readonly GraphEvidenceLookup lookup;
    private readonly List<string> failed = [];

    public BuildLinkGraphMaterializer(DatabaseOptions options, IRelationshipGraphStore graph)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.graph = graph ?? throw new ArgumentNullException(nameof(graph));
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
        lookup = new GraphEvidenceLookup(source);
    }

    /// <summary>Builds whose links could not be projected (error type only); the others still are.</summary>
    public IReadOnlyList<string> Failed => failed;

    public async Task<int> ReconcileAsync(string sourceInstance, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstance);
        failed.Clear();
        var rows = new List<(string Project, string Build, string Repo, string? Sha, string? Pr, string Ref)>();
        await using (var connection = await source.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand("""
            SELECT project_id, build_id, repository_id, commit_sha, pull_request_id, evidence_ref
            FROM evidence.build_links WHERE source_instance = @source
            ORDER BY project_id, build_id
            """, connection))
        {
            command.Parameters.AddWithValue("source", sourceInstance);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5)));
            }
        }

        var written = 0;
        foreach (var row in rows)
        {
            try
            {
                if (!GraphEvidenceLookup.TryParseReference(row.Ref, out var itemKey, out var revision) ||
                    await lookup.RevisionTimeAsync(itemKey, revision, cancellationToken) is not { } assertedAt ||
                    await lookup.FindAsync(sourceInstance, row.Project, row.Repo, "ci_run", row.Build, cancellationToken) is not { } build)
                {
                    continue; // never assert without the cited, stored build evidence
                }

                var buildNode = new GraphNode(build, GraphNodeKind.CiRun, row.Build);
                if (row.Sha is { } sha && await lookup.FindAsync(sourceInstance, row.Project, row.Repo, "commit", sha, cancellationToken) is { } commit)
                {
                    written += await AssertAsync(buildNode, new GraphNode(commit, GraphNodeKind.Commit, sha), GraphRelationKind.CiCommit, itemKey, revision, assertedAt, cancellationToken);
                }

                if (row.Pr is { } pr && await lookup.FindAsync(sourceInstance, row.Project, row.Repo, "pull_request", pr, cancellationToken) is { } pullRequest)
                {
                    written += await AssertAsync(buildNode, new GraphNode(pullRequest, GraphNodeKind.PullRequest, pr), GraphRelationKind.CiPullRequest, itemKey, revision, assertedAt, cancellationToken);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failed.Add($"build {row.Project}/{row.Build}: {error.GetType().Name}");
            }
        }

        return written;
    }

    private async Task<int> AssertAsync(
        GraphNode from, GraphNode to, GraphRelationKind relation, string itemKey, string revision, DateTimeOffset assertedAt, CancellationToken cancellationToken)
    {
        await graph.SaveAsync(new GraphAssertion(from, to, relation, itemKey, revision,
            GraphClassification.Explicit, GraphAssertionState.Active, assertedAt), cancellationToken);
        return 1;
    }

    public ValueTask DisposeAsync() => source.DisposeAsync();
}
