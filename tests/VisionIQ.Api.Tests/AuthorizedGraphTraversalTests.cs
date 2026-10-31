using VisionIQ.Api.Features.Evidence;
using VisionIQ.Api.Features.Graph;

namespace VisionIQ.Api.Tests;

/// <summary>LRN-07 bounded graph traversal and source-permission regression coverage.</summary>
public sealed class AuthorizedGraphTraversalTests
{
    private static readonly string A = new('a', 64);
    private static readonly string B = new('b', 64);
    private static readonly string C = new('c', 64);
    private static readonly string D = new('d', 64);

    /// <summary>Constructs a trusted caller whose approved group appears in test ACLs.</summary>
    private static EvidenceRequester Requester() =>
        new("reviewer", new HashSet<string> { "approved-group" });

    /// <summary>Canonical identity with one provider and strictly source-scoped project.</summary>
    private static EvidenceReadCandidate Candidate(string anchor) =>
        new(new EvidenceIdentity("azure_devops", "https://dev.azure.com/approved",
                "project-a", "repo-a", "commit", anchor),
            "rev-7", "https://dev.azure.com/approved/project-a",
            """{"kind":"commit"}""", true, ["approved-group"]);

    [Fact]
    public async Task Authorized_three_hop_chain_is_traversable_with_opaque_ids()
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
        var service = Service(store, new StubAccess());
        var graph = await service.TraverseAsync(nodes[0].NodeKey(), Requester());

        Assert.Equal(4, graph.Nodes.Count);
        Assert.Equal(3, graph.Edges.Count);
        Assert.All(graph.Edges, edge =>
        {
            Assert.Equal(GraphClassification.Explicit, edge.Classification);
            Assert.DoesNotContain("module.py", edge.ToNodeKey);
        });
    }

    [Fact]
    public async Task A_source_permission_revocation_hides_the_endpoint_and_edge()
    {
        var src = new GraphNode(A, GraphNodeKind.WorkItem, "123");
        var dst = new GraphNode(B, GraphNodeKind.PullRequest, "42");
        var store = new FakeGraph([src, dst]);
        store.Link(src, dst, GraphRelationKind.TicketPr, A);
        var access = new StubAccess { Deny = B };
        var graph = await Service(store, access).TraverseAsync(src.NodeKey(), Requester());
        Assert.Single(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public async Task Unknown_or_unverified_provenance_revision_is_never_exposed()
    {
        var src = new GraphNode(A, GraphNodeKind.WorkItem, "123");
        var dst = new GraphNode(B, GraphNodeKind.PullRequest, "42");
        var store = new FakeGraph([src, dst]);
        store.Link(src, dst, GraphRelationKind.TicketPr, C);
        var access = new StubAccess { DenyRevision = C };
        var graph = await Service(store, access).TraverseAsync(src.NodeKey(), Requester());
        Assert.Single(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public async Task Inaccessible_start_node_never_queries_neighbors()
    {
        var src = new GraphNode(A, GraphNodeKind.WorkItem, "123");
        var store = new FakeGraph([src]);
        var graph = await Service(store, new StubAccess { Deny = A })
            .TraverseAsync(src.NodeKey(), Requester());
        Assert.Empty(graph.Nodes);
        Assert.Empty(graph.Edges);
        Assert.Equal(0, store.AdjacencyReads);
    }

    [Fact]
    public async Task Hop_limit_prevents_deeper_expansion()
    {
        var src = new GraphNode(A, GraphNodeKind.WorkItem, "123");
        var dst = new GraphNode(B, GraphNodeKind.PullRequest, "42");
        var third = new GraphNode(C, GraphNodeKind.Commit, new string('c', 40));
        var store = new FakeGraph([src, dst, third]);
        store.Link(src, dst, GraphRelationKind.TicketPr, A);
        store.Link(dst, third, GraphRelationKind.PrCommit, B);
        var output = await Service(store, new StubAccess())
            .TraverseAsync(src.NodeKey(), Requester(), maxHops: 1);
        Assert.Equal(2, output.Nodes.Count);
        Assert.Single(output.Edges);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Service(store, new StubAccess()).TraverseAsync(src.NodeKey(), Requester(), maxHops: 5));
    }

    [Fact]
    public void Keys_are_stable_scoped_and_collision_resistant()
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

    /// <summary>Constructs the internal ACL-aware traversal with fake provider authorization.</summary>
    private static AuthorizedGraphTraversal Service(FakeGraph graph, StubAccess access) =>
        new(graph,
            new EvidenceQueryService(new StubReader(access), new StubProvider(access)),
            access);

    /// <summary>Provides source-scoped graph nodes and explicit adjacency.</summary>
    private sealed class FakeGraph(IEnumerable<GraphNode> graphNodes) : IRelationshipGraphStore
    {
        private readonly Dictionary<string, GraphNode> nodes = graphNodes.ToDictionary(node => node.NodeKey());
        private readonly Dictionary<string, List<GraphNeighbor>> edges = [];
        public int AdjacencyReads { get; private set; }

        /// <summary>Adds one explicit test edge without a live provider record.</summary>
        public void Link(GraphNode source, GraphNode target, GraphRelationKind relation, string proof)
        {
            edges.TryAdd(source.NodeKey(), []);
            edges[source.NodeKey()].Add(
                new GraphNeighbor(target.NodeKey(), target.AnchorItemKey, proof, "rev-7",
                    relation, GraphClassification.Explicit));
        }

        /// <summary>Return the test node matching its exact opaque key.</summary>
        public Task<GraphNode?> GetNodeAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(nodes.GetValueOrDefault(key));

        /// <summary>Return only edges owned by the requested parent.</summary>
        public Task<IReadOnlyList<GraphNeighbor>> OutgoingAsync(
            string key, int limit, CancellationToken cancellationToken = default)
        {
            AdjacencyReads++;
            return Task.FromResult<IReadOnlyList<GraphNeighbor>>(
                edges.GetValueOrDefault(key)?.Take(limit).ToArray() ?? []);
        }

        /// <summary>Unused in traversal-only fixture tests.</summary>
        public Task SaveAsync(GraphAssertion assertion, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>Simulates a provider revoke and a historically denied ACL revision.</summary>
    private sealed class StubAccess : IGraphRevisionAccess
    {
        public string? Deny { get; init; }
        public string? DenyRevision { get; init; }

        /// <summary>Check exact provenance revision ACL independently of current access.</summary>
        public Task<bool> CanReadRevisionAsync(string key, string rev,
            EvidenceRequester requester, CancellationToken cancellationToken = default) =>
            Task.FromResult(key != DenyRevision && rev == "rev-7");
    }

    /// <summary>Reads only fabricated canonical access metadata for test keys.</summary>
    private sealed class StubReader(StubAccess access) : ICanonicalEvidenceReader
    {
        /// <summary>Return the latest safe canonical revision for the requested item.</summary>
        public Task<EvidenceReadCandidate?> FindLatestAsync(
            string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<EvidenceReadCandidate?>(key == access.Deny ? null : Candidate(key));
    }

    /// <summary>Models a fresh, authenticated provider permission check.</summary>
    private sealed class StubProvider(StubAccess access) : IProviderAccessVerifier
    {
        /// <summary>Fail closed for a provider-denied source identity.</summary>
        public Task<bool> CanReadNowAsync(EvidenceIdentity identity, EvidenceRequester requester,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(identity.ExternalId != access.Deny);
    }
}
