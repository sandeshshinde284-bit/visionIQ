using VisionIQ.Api.Features.Evidence;
using VisionIQ.Api.Features.Graph;
using VisionIQ.Api.Features.GraphApi;

namespace VisionIQ.Api.Tests;

/// <summary>Graph query results: "not found" and "not allowed" look the same; nothing unauthorized appears.</summary>
public sealed class GraphQueryServiceTests
{
    private const string Org = "https://dev.azure.com/contoso";
    private static readonly GraphApiOptions Options = new(Guid.NewGuid(), ["api://visioniq"], "Graph.Read",
        new HashSet<string>(), Org, ["Payments"]);

    private static string ItemKey(string id, string kind = "work_item_current", string project = "Payments") =>
        new EvidenceIdentity("azure_devops", Org, project, null, kind, id).ItemKey();

    private sealed class Graph : IRelationshipGraphStore
    {
        public Dictionary<string, GraphNode> Nodes { get; } = new(StringComparer.Ordinal);
        public List<(GraphNode From, GraphNode To, GraphRelationKind Relation, string Provenance)> Edges { get; } = [];

        public string Add(GraphNode node)
        {
            Nodes[node.NodeKey()] = node;
            return node.NodeKey();
        }

        public Task SaveAsync(GraphAssertion assertion, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<GraphNode?> GetNodeAsync(string nodeKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(Nodes.GetValueOrDefault(nodeKey));

        public Task<IReadOnlyList<GraphNeighbor>> OutgoingAsync(string nodeKey, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GraphNeighbor>>(Edges.Where(e => e.From.NodeKey() == nodeKey)
                .Select(e => new GraphNeighbor(e.To.NodeKey(), e.To.AnchorItemKey, e.Provenance, "r1", e.Relation, GraphClassification.Explicit))
                .ToList());
    }

    /// <summary>Evidence the requester may read; anything else is unreadable.</summary>
    private sealed class Reader(HashSet<string> readable, Dictionary<string, EvidenceIdentity> identities) : ICanonicalEvidenceReader
    {
        public Task<EvidenceReadCandidate?> FindLatestAsync(string itemKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(identities.TryGetValue(itemKey, out var identity) && readable.Contains(itemKey)
                ? new EvidenceReadCandidate(identity, "r1", "https://dev.azure.com/x", "{\"state\":\"Active\"}", true, ["ado-project:p"])
                : null);
    }

    private sealed class Allow : IProviderAccessVerifier
    {
        public Task<bool> CanReadNowAsync(EvidenceIdentity identity, EvidenceRequester requester, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class Revisions(EvidenceQueryService evidence) : IGraphRevisionAccess
    {
        public async Task<bool> CanReadRevisionAsync(string itemKey, string revision, EvidenceRequester requester, CancellationToken cancellationToken = default) =>
            await evidence.FindLatestAuthorizedAsync(itemKey, requester, cancellationToken) is not null;
    }

    private static readonly EvidenceRequester Ana = new("ana@contoso.com", new HashSet<string> { "ado-project:p" });

    private static (GraphQueryService Service, Graph Graph, HashSet<string> Readable) Build()
    {
        var graph = new Graph();
        var readable = new HashSet<string>(StringComparer.Ordinal);
        var identities = new Dictionary<string, EvidenceIdentity>(StringComparer.Ordinal);
        foreach (var (id, kind) in new[] { ("1", "work_item_current"), ("2", "pull_request"), ("3", "pull_request") })
        {
            identities[ItemKey(id, kind)] = new EvidenceIdentity("azure_devops", Org, "Payments", null, kind, id);
        }

        var evidence = new EvidenceQueryService(new Reader(readable, identities), new Allow());
        var traversal = new AuthorizedGraphTraversal(graph, evidence, new Revisions(evidence));
        var bug = new GraphNode(ItemKey("1"), GraphNodeKind.WorkItem, "1");
        var visible = new GraphNode(ItemKey("2", "pull_request"), GraphNodeKind.PullRequest, "2");
        var hidden = new GraphNode(ItemKey("3", "pull_request"), GraphNodeKind.PullRequest, "3");
        graph.Add(bug);
        graph.Add(visible);
        graph.Add(hidden);
        graph.Edges.Add((bug, visible, GraphRelationKind.TicketPr, ItemKey("1")));
        graph.Edges.Add((bug, hidden, GraphRelationKind.TicketPr, ItemKey("1")));
        readable.UnionWith([ItemKey("1"), ItemKey("2", "pull_request")]);
        return (new GraphQueryService(traversal, evidence, Options), graph, readable);
    }

    [Fact]
    public async Task Only_authorized_nodes_and_edges_are_returned_with_display_details()
    {
        var (service, _, _) = Build();

        var result = (await service.QueryWorkItemAsync("payments", "1", Ana, 2))!;

        Assert.Equal(new[] { "1", "2" }, result.Nodes.Select(n => n.NativeId).Order());
        var edge = Assert.Single(result.Edges);
        Assert.Equal(("ticket_pr", "explicit"), (edge.Relation, edge.Classification));
        var pr = Assert.Single(result.Nodes, n => n.NativeId == "2");
        Assert.Equal(("pull_request", "pull_request", "Payments", "Active"), (pr.Kind, pr.SourceKind, pr.Project, pr.Metadata.GetProperty("state").GetString()));
    }

    [Fact]
    public async Task Not_found_and_not_allowed_are_indistinguishable()
    {
        var (service, _, readable) = Build();

        Assert.Null(await service.QueryWorkItemAsync("Payments", "999", Ana, 2)); // does not exist
        readable.Remove(ItemKey("1"));
        Assert.Null(await service.QueryWorkItemAsync("Payments", "1", Ana, 2));   // exists, not allowed
    }

    [Theory]
    [InlineData("Secret-Project", "1")]
    [InlineData("Payments", "1; DROP")]
    [InlineData("Payments", "")]
    [InlineData("Payments", "1234567890123456789")]
    public async Task Unapproved_projects_and_malformed_ids_look_like_not_found(string project, string id)
    {
        var (service, _, _) = Build();

        Assert.Null(await service.QueryWorkItemAsync(project, id, Ana, 2));
    }

    [Fact]
    public async Task Hop_limits_are_enforced()
    {
        var (service, _, _) = Build();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.QueryWorkItemAsync("Payments", "1", Ana, 4));
    }
}
