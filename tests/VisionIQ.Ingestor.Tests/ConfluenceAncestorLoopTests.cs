using VisionIQ.Ingestor.Confluence;

namespace VisionIQ.Ingestor.Tests;

/// <summary>M-2: a malformed ancestor list must make the page's ACL incomplete (hidden), never trusted.</summary>
public sealed class ConfluenceAncestorLoopTests
{
    private static ConfluencePage Page(string id, params string[] ancestors) =>
        new(id, "ENG", "Page " + id, 1, DateTimeOffset.UnixEpoch) { AncestorIds = ancestors };

    [Fact]
    public void An_ancestor_list_containing_the_page_itself_is_incomplete()
    {
        var acls = ConfluenceAccess.ComputeAcls([Page("1"), Page("3", "1", "3")]);

        Assert.False(acls["3"].Complete);
    }

    [Fact]
    public void A_repeated_ancestor_is_incomplete()
    {
        var acls = ConfluenceAccess.ComputeAcls([Page("1"), Page("2", "1"), Page("4", "1", "2", "1")]);

        Assert.False(acls["4"].Complete);
    }

    [Fact]
    public void A_normal_ancestor_list_is_still_complete()
    {
        var acls = ConfluenceAccess.ComputeAcls([Page("1"), Page("2", "1"), Page("4", "1", "2")]);

        Assert.True(acls["4"].Complete);
    }
}
