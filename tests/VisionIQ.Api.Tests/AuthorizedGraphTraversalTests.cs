using VisionIQ.Api.Features.Evidence;
using VisionIQ.Api.Features.Graph;

namespace VisionIQ.Api.Tests;

/// <summary>
/// LRN-07 internal graph-structure tests. User-level ACL integration is deferred.
/// These tests never represent an authenticated public API or permission grant.
/// </summary>
public sealed class AuthorizedGraphTraversalTests
{
    private static readonly string A = new('a', 64);
    private static readonly string B = new('b', 64);
    private static readonly string C = new('c', 64);
    private static readonly string D = new('d', 64);

    private static EvidenceRequester LocalOperator() => new("internal-graph-test", new HashSet<string>());

    [Fact]
    public async Task Explicit_three_hop_ticket_pr_commit_file_chain_is_traversable()
    {
        var nodes = new[]
        {
            new GraphNode(A, GraphNodeKind.WorkItem, "123"),
            new GraphNode(B, GraphNodeKind.PullRequest, "42"),
            new GraphNode(C, GraphNodeKind.Commit, new string('f', 40)),
            new GraphNode(D, GraphNodeKind.File, "src/module.py"),
        };
        var store = new FakeGraph(nodes);
        store.Link(nodes[0], nodes[1], GraphRelationKind.TicketPr, A);
        store.Link(nodes[1], nodes[2], GraphRelationKind.PrCommit, B);
        store.Link(nodes[2], nodes[3], GraphRelationKind.CommitFile, C);

        var graph = await new AuthorizedGraphTraversal(store)
            .TraverseAsync(nodes[0].NodeKey(), LocalOperator());

        Assert.Equal(4, graph.Nodes.Count);
        Assert.Equal(3, graph.Edges.Count);
        Assert.Equal(new[]
        {
            GraphRelationKind.TicketPr,
            GraphRelationKind.PrCommit,
            GraphRelationKind.CommitFile,
        }, graph.Edges.Select(edge => edge.Relation));
        Assert.All(graph.Edges, edge =>
        {
            Assert.Equal(GraphClassification.Explicit, edge.Classification);
            Assert.DoesNotContain("module.py", edge.ToNodeKey);
        });
    }

    [Fact]
    public async Task Missing_destination_is_not_invented()
    {
        var source = new GraphNode(A, GraphNodeKind.WorkItem, "123");
        var missing = new GraphNode(B, GraphNodeKind.PullRequest, "42");
        var store = new FakeGraph([source]);
        store.Link(source, missing, GraphRelationKind.TicketPr, A);

        var graph = await new AuthorizedGraphTraversal(store)
            .TraverseAsync(source.NodeKey(), LocalOperator());

        Assert.Single(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public async Task Mismatched_destination_anchor_is_rejected()
    {
        var source = new GraphNode(A, GraphNodeKind.WorkItem, "123");
        var target = new GraphNode(B, GraphNodeKind.PullRequest, "42");
        var store = new FakeGraph([source, target]);
        store.Link(source, target, GraphRelationKind.TicketPr, A, reportedAnchor: C);

        var graph = await new AuthorizedGraphTraversal(store)
            .TraverseAsync(source.NodeKey(), LocalOperator());

        Assert.Single(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public async Task Nonexistent_root_cannot_traverse_any_edges()
    {
        var store = new FakeGraph([]);
        var graph = await new AuthorizedGraphTraversal(store).TraverseAsync(A, LocalOperator());

        Assert.Empty(graph.Nodes);
        Assert.Empty(graph.Edges);
        Assert.Equal(0, store.AdjacencyReads);
    }

    [Fact]
    public async Task Hop_and_node_limits_are_enforced()
    {
        var root = new GraphNode(A, GraphNodeKind.WorkItem, "123");
        var pr = new GraphNode(B, GraphNodeKind.PullRequest, "42");
        var commit = new GraphNode(C, GraphNodeKind.Commit, new string('c', 40));
        var store = new FakeGraph([root, pr, commit]);
        store.Link(root, pr, GraphRelationKind.TicketPr, A);
        store.Link(pr, commit, GraphRelationKind.PrCommit, B);

        var traversal = new AuthorizedGraphTraversal(store);
        var oneHop = await traversal.TraverseAsync(root.NodeKey(), LocalOperator(), maxHops: 1);
        Assert.Equal(2, oneHop.Nodes.Count);
        Assert.Single(oneHop.Edges);

        var oneNode = await traversal.TraverseAsync(root.NodeKey(), LocalOperator(), maxNodes: 1);
        Assert.Single(oneNode.Nodes);
        Assert.Empty(oneNode.Edges);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            traversal.TraverseAsync(root.NodeKey(), LocalOperator(), maxHops: 4));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            traversal.TraverseAsync(root.NodeKey(), LocalOperator(), maxNodes: 101));
    }

    [Fact]
    public void Keys_are_stable_source_scoped_and_collision_resistant()
    {
        var same = new GraphNode(A, GraphNodeKind.File, "src/a.py");
        Assert.Equal(same.NodeKey(), same.NodeKey());
        Assert.NotEqual(same.NodeKey(), (same with { AnchorItemKey = B }).NodeKey());
        Assert.NotEqual(same.NodeKey(), (same with { Kind = GraphNodeKind.Symbol }).NodeKey());
        Assert.Throws<ArgumentException>(() => (same with { AnchorItemKey = "invalid" }).NodeKey());

        var assertion = new GraphAssertion(
            new GraphNode(A, GraphNodeKind.WorkItem, "123"),
            new GraphNode(B, GraphNodeKind.PullRequest, "42"),
            GraphRelationKind.TicketPr, C, "rev-1", GraphClassification.Explicit,
            GraphAssertionState.Active, DateTimeOffset.UtcNow);
        Assert.Equal(assertion.AssertionKey(), assertion.AssertionKey());
        Assert.NotEqual(assertion.AssertionKey(),
            (assertion with { ProvenanceRevision = "rev-2" }).AssertionKey());
    }

    private sealed class FakeGraph(IEnumerable<GraphNode> graphNodes) : IRelationshipGraphStore
    {
        private readonly Dictionary<string, GraphNode> nodes =
            graphNodes.ToDictionary(node => node.NodeKey());
        private readonly Dictionary<string, List<GraphNeighbor>> edges = [];
        public int AdjacencyReads { get; private set; }

        public void Link(
            GraphNode source, GraphNode target, GraphRelationKind relation,
            string proof, string? reportedAnchor = null)
        {
            edges.TryAdd(source.NodeKey(), []);
            edges[source.NodeKey()].Add(
                new GraphNeighbor(target.NodeKey(), reportedAnchor ?? target.AnchorItemKey,
                    proof, "rev-7", relation, GraphClassification.Explicit));
        }

        public Task<GraphNode?> GetNodeAsync(
            string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(nodes.GetValueOrDefault(key));

        public Task<IReadOnlyList<GraphNeighbor>> OutgoingAsync(
            string key, int limit, CancellationToken cancellationToken = default)
        {
            AdjacencyReads++;
            return Task.FromResult<IReadOnlyList<GraphNeighbor>>(
                edges.GetValueOrDefault(key)?.Take(limit).ToArray() ?? []);
        }

        public Task SaveAsync(
            GraphAssertion assertion, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
