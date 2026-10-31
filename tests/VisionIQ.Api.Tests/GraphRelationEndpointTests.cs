using VisionIQ.Api.Features.Graph;

namespace VisionIQ.Api.Tests;

/// <summary>M-3: a relationship may only connect the node kinds it is defined for.</summary>
public sealed class GraphRelationEndpointTests
{
    private static readonly string A = new('a', 64), B = new('b', 64), C = new('c', 64);

    private static GraphAssertion Edge(GraphNodeKind from, GraphNodeKind to, GraphRelationKind relation) =>
        new(new GraphNode(A, from, "1"), new GraphNode(B, to, "2"), relation, C, "rev-1",
            GraphClassification.Explicit, GraphAssertionState.Active, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(GraphNodeKind.WorkItem, GraphNodeKind.PullRequest, GraphRelationKind.TicketPr)]
    [InlineData(GraphNodeKind.PullRequest, GraphNodeKind.Commit, GraphRelationKind.PrCommit)]
    [InlineData(GraphNodeKind.WorkItem, GraphNodeKind.Commit, GraphRelationKind.TicketCommit)]
    [InlineData(GraphNodeKind.Commit, GraphNodeKind.File, GraphRelationKind.CommitFile)]
    [InlineData(GraphNodeKind.File, GraphNodeKind.Repository, GraphRelationKind.RepositoryDependency)]
    public void Valid_endpoints_are_accepted(GraphNodeKind from, GraphNodeKind to, GraphRelationKind relation) =>
        Edge(from, to, relation).Validate();

    [Theory]
    [InlineData(GraphNodeKind.Commit, GraphNodeKind.Commit, GraphRelationKind.TicketPr)]
    [InlineData(GraphNodeKind.PullRequest, GraphNodeKind.WorkItem, GraphRelationKind.TicketPr)]
    [InlineData(GraphNodeKind.File, GraphNodeKind.Commit, GraphRelationKind.CommitFile)]
    public void Mismatched_endpoints_are_rejected(GraphNodeKind from, GraphNodeKind to, GraphRelationKind relation) =>
        Assert.Throws<ArgumentException>(() => Edge(from, to, relation).Validate());
}
