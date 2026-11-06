using Npgsql;
using VisionIQ.Api.Configuration;

namespace VisionIQ.Api.Features.Graph;

/// <summary>
/// LRN-07: projects declared test links into the graph, keeping the evidence levels separate:
/// <list type="bullet">
/// <item><c>tested_by</c>: a work item is associated with an ADO Test Case ("Tested By"). Declared only; not proof
/// that a test passed, covers the affected code, or prevents recurrence.</item>
/// <item><c>automated_by</c>: the Test Case's automated test name matches exactly ONE test method in the latest
/// code scan. Zero or several matches are reported as unresolved and produce no edge.</item>
/// </list>
/// Both are shown only while their evidence is current (see the traversal query), so a removed link or a
/// renamed test disappears without rewriting history.
/// </summary>
public sealed class TestLinkGraphMaterializer : IAsyncDisposable
{
    /// <summary>Test methods present in the latest scan whose qualified name equals the automated test name.</summary>
    // native_id is "<file>::<qualified.method.name>:<line>"; the substring strips the file prefix and
    // trailing line number so a line-number-only diff between scans never breaks the match. LIMIT 3 is
    // enough to tell "exactly one" from "more than one" without scanning every hit in a large repo.
    public const string MappingSql = """
        SELECT DISTINCT n.node_key, n.anchor_item_key, n.native_id
        FROM evidence.graph_assertions declared
        JOIN evidence.graph_nodes n ON n.node_key = declared.to_node_key
        JOIN evidence.source_items scan ON scan.item_key = declared.provenance_item_key
        WHERE declared.relation_type = 'file_test'
          AND n.node_kind = 'test_case'
          AND declared.provenance_revision_id = scan.latest_revision_id
          AND substring(n.native_id from '::(.*):[0-9]+$') = @name
        ORDER BY n.node_key
        LIMIT 3
        """;

    private readonly NpgsqlDataSource source;
    private readonly IRelationshipGraphStore graph;
    private readonly GraphEvidenceLookup lookup;
    private readonly List<string> unresolved = [];
    private readonly List<string> failed = [];

    public TestLinkGraphMaterializer(DatabaseOptions options, IRelationshipGraphStore graph)
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
        lookup = new GraphEvidenceLookup(source);
    }

    /// <summary>Test Cases whose automated test name matched no test method, or more than one.</summary>
    public IReadOnlyList<string> Unresolved => unresolved;

    /// <summary>Links that could not be projected (error type only); the others still are.</summary>
    public IReadOnlyList<string> Failed => failed;

    public async Task<int> ReconcileAsync(string sourceInstance, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstance);
        unresolved.Clear();
        failed.Clear();
        var written = 0;

        foreach (var (project, workItem, testCase, reference) in await ReadAsync("""
            SELECT project_id, work_item_id, test_case_id, evidence_ref
            FROM evidence.tested_by_links WHERE source_instance = @source
            ORDER BY project_id, work_item_id, test_case_id
            """, sourceInstance, cancellationToken))
        {
            written += await IsolatedAsync($"tested_by {project}/{workItem}->{testCase}", async () =>
            {
                var from = await FindWorkItemAsync(sourceInstance, project, workItem, cancellationToken);
                var to = await FindWorkItemAsync(sourceInstance, project, testCase, cancellationToken);
                return from is null || to is null
                    ? 0
                    : await AssertAsync(new GraphNode(from, GraphNodeKind.WorkItem, workItem),
                        new GraphNode(to, GraphNodeKind.WorkItem, testCase), GraphRelationKind.TestedBy, reference, cancellationToken);
            });
        }

        foreach (var (project, testCase, name, reference) in await ReadAsync("""
            SELECT project_id, test_case_id, automated_test_name, evidence_ref
            FROM evidence.test_case_automation WHERE source_instance = @source
            ORDER BY project_id, test_case_id
            """, sourceInstance, cancellationToken))
        {
            written += await IsolatedAsync($"automated_by {project}/{testCase}", async () =>
            {
                var from = await FindWorkItemAsync(sourceInstance, project, testCase, cancellationToken);
                if (from is null)
                {
                    return 0;
                }

                var matches = await MatchTestMethodsAsync(name, cancellationToken);
                if (matches.Count != 1)
                {
                    unresolved.Add($"Test Case {project}/{testCase}: '{name}' matched {(matches.Count == 0 ? "no" : "more than one")} test method");
                    return 0;
                }

                var (nodeAnchor, nativeId) = matches[0];
                return await AssertAsync(new GraphNode(from, GraphNodeKind.WorkItem, testCase),
                    new GraphNode(nodeAnchor, GraphNodeKind.TestCase, nativeId), GraphRelationKind.AutomatedBy, reference, cancellationToken);
            });
        }

        return written;
    }

    private async Task<int> IsolatedAsync(string label, Func<Task<int>> work)
    {
        try
        {
            return await work();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            failed.Add($"{label}: {error.GetType().Name}");
            return 0;
        }
    }

    private async Task<int> AssertAsync(
        GraphNode from, GraphNode to, GraphRelationKind relation, string reference, CancellationToken cancellationToken)
    {
        if (!TryParseReference(reference, out var itemKey, out var revision) ||
            await lookup.RevisionTimeAsync(itemKey, revision, cancellationToken) is not { } assertedAt)
        {
            return 0; // the cited evidence revision is not stored (yet): never assert without proof
        }

        await graph.SaveAsync(new GraphAssertion(from, to, relation, itemKey, revision,
            GraphClassification.Explicit, GraphAssertionState.Active, assertedAt), cancellationToken);
        return 1;
    }

    private async Task<List<(string Anchor, string NativeId)>> MatchTestMethodsAsync(string name, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(MappingSql, connection);
        command.Parameters.AddWithValue("name", name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var matches = new List<(string, string)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            matches.Add((reader.GetString(1), reader.GetString(2)));
        }

        return matches;
    }

    private async Task<List<(string, string, string, string)>> ReadAsync(string sql, string sourceInstance, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("source", sourceInstance);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<(string, string, string, string)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return rows;
    }

    private Task<string?> FindWorkItemAsync(string instance, string project, string id, CancellationToken cancellationToken) =>
        lookup.FindAsync(instance, project, null, "work_item_current", id, cancellationToken);

    /// <summary>Parses "evidence:&lt;64-hex item key&gt;:&lt;revision&gt;".</summary>
    public static bool TryParseReference(string value, out string itemKey, out string revision) =>
        GraphEvidenceLookup.TryParseReference(value, out itemKey, out revision);

    public ValueTask DisposeAsync() => source.DisposeAsync();
}
