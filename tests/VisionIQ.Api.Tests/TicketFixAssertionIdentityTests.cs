using VisionIQ.Api.Features.Graph;

namespace VisionIQ.Api.Tests;

public sealed class TicketFixAssertionIdentityTests
{
    private static readonly string Ticket = new('a', 64);
    private static readonly string Pr = new('b', 64);
    private static readonly string Provenance = new('c', 64);
    private static readonly string AssociationA = new('d', 64);
    private static readonly string AssociationB = new('e', 64);

    [Fact]
    public void Distinct_ticket_fix_associations_with_same_source_revision_have_distinct_assertion_keys()
    {
        var first = Make(AssociationA);
        var second = Make(AssociationB);

        Assert.Equal(first.RelationshipKey(), second.RelationshipKey());
        Assert.NotEqual(first.AssertionKey(), second.AssertionKey());
        Assert.NotEqual(first.ContentHash(), second.ContentHash());
        Assert.Equal(first.AssertionKey(), Make(AssociationA).AssertionKey());
        Assert.Equal(first.ContentHash(), Make(AssociationA).ContentHash());
    }

    [Fact]
    public void Assertions_without_ticket_fix_association_retain_legacy_key()
    {
        var assertion = Make(null);
        Assert.Equal(GraphHashes.Hash(assertion.RelationshipKey(), assertion.ProvenanceRevision), assertion.AssertionKey());
    }

    [Fact]
    public void Association_specific_key_preserves_immutable_content_collision_detection()
    {
        var original = Make(AssociationA);
        var changed = original with { State = GraphAssertionState.Retracted };

        Assert.Equal(original.AssertionKey(), changed.AssertionKey());
        Assert.NotEqual(original.ContentHash(), changed.ContentHash());
    }

    private static GraphAssertion Make(string? association) =>
        new(
            new GraphNode(Ticket, GraphNodeKind.WorkItem, "123"),
            new GraphNode(Pr, GraphNodeKind.PullRequest, "456"),
            GraphRelationKind.TicketPr,
            Provenance,
            "7",
            GraphClassification.Explicit,
            GraphAssertionState.Active,
            new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero),
            association);
}
