using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Links;

namespace VisionIQ.Ingestor.Tests;

public sealed class LinkExtractorTests
{
    [Fact]
    public void Work_item_artifact_links_point_to_pull_requests_commits_builds_and_related_items()
    {
        var workItem = new WorkItemRecord(1234, 7, "Bug", "Resolved", "Retry loop", [], DateTimeOffset.UnixEpoch)
        {
            Relations =
            [
                new("ArtifactLink", "vstfs:///Git/PullRequestId/proj-1%2Frepo-1%2F77", "Pull Request"),
                new("ArtifactLink", "vstfs:///Git/Commit/proj-1%2Frepo-1%2F" + DemoAdo.Commit, "Fixed in Commit"),
                new("ArtifactLink", "vstfs:///Build/Build/501", "Build"),
                new("System.LinkTypes.Related", "https://dev.azure.com/contoso/_apis/wit/workItems/1240", null),
                new("Hyperlink", "https://example.com/postmortem", null),
            ],
        };

        var links = LinkExtractor.FromWorkItem(workItem);

        Assert.Equal(4, links.Count);
        Assert.Contains(links, link => link.ToKind == EntityKind.PullRequest && link.ToId == "77");
        Assert.Contains(links, link => link.ToKind == EntityKind.Commit && link.ToId == DemoAdo.Commit);
        Assert.Contains(links, link => link.ToKind == EntityKind.Build && link.ToId == "501");
        Assert.Contains(links, link => link.ToKind == EntityKind.WorkItem && link.ToId == "1240" && link.LinkType == "work_item_relation");
    }

    [Fact]
    public void Pull_request_links_come_from_its_work_items_commits_and_merge_commit()
    {
        var pullRequest = new PullRequestRecord(77, "repo-1", "payments-service", DemoAdo.Project, "completed", "Fix", DateTimeOffset.UnixEpoch)
        {
            WorkItemIds = [1234],
            CommitIds = [DemoAdo.Commit],
            MergeCommitId = DemoAdo.MergeCommit,
        };

        var links = LinkExtractor.FromPullRequest(pullRequest);

        Assert.Equal(new[] { "pr_work_item", "pr_commit", "pr_merge_commit" }, links.Select(link => link.LinkType));
    }

    [Fact]
    public void Commit_messages_give_merge_revert_and_work_item_links()
    {
        var merge = LinkExtractor.FromCommit(Commit(DemoAdo.MergeCommit, "Merged PR 77: Fix retry loop"));
        var revert = LinkExtractor.FromCommit(Commit(DemoAdo.RevertCommit, "Revert \"x\"\n\nThis reverts commit " + DemoAdo.Commit + "."));
        var mentions = LinkExtractor.FromCommit(Commit(DemoAdo.Commit, "Stop retrying #1234\n\nAlso AB#1240"));

        Assert.Equal("77", Assert.Single(merge).ToId);
        var revertLink = Assert.Single(revert);
        Assert.Equal("reverts", revertLink.LinkType);
        Assert.Equal(DemoAdo.Commit, revertLink.ToId);
        Assert.Equal(new[] { "1240", "1234" }, mentions.Select(link => link.ToId));
    }

    [Theory]
    [InlineData("see https://wiki.example/page#55")]
    [InlineData("html entity &#123; in text")]
    [InlineData("step #1 of 3")]
    [InlineData("see PR #42 for context")]
    [InlineData("deployed on 2026-10-20 at 10:15, same day as the bug")]
    public void Noise_never_becomes_a_link(string message)
    {
        Assert.Empty(LinkExtractor.FromCommit(Commit(DemoAdo.Commit, message)));
    }

    [Fact]
    public void Build_links_point_to_its_commit_and_pull_request()
    {
        var build = new BuildRecord(501, DemoAdo.Project, "20261019.3", 12, "payments-ci", "completed", DateTimeOffset.UnixEpoch)
        {
            SourceVersion = DemoAdo.MergeCommit,
            PullRequestNumber = 77,
        };

        var links = LinkExtractor.FromBuild(build);

        Assert.Equal(new[] { "build_source_version", "build_pull_request" }, links.Select(link => link.LinkType));
    }

    [Fact]
    public void A_repeated_reference_is_kept_once()
    {
        Assert.Single(LinkExtractor.FromCommit(Commit(DemoAdo.Commit, "Fix #1234 and again #1234")));
    }

    private static CommitRecord Commit(string id, string message) => new(id, "repo-1", DemoAdo.Project, DateTimeOffset.UnixEpoch, message);
}
