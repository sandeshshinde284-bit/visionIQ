using System.Text.Json;
using VisionIQ.Ingestor.Ado;

namespace VisionIQ.Ingestor.Tests;

public sealed class AdoSourceTests
{
    [Fact]
    public async Task Work_item_query_uses_time_precision_and_starts_at_the_checkpoint()
    {
        var handler = DemoAdo.Server();
        var source = new AdoWorkItemSource(DemoAdo.Client(handler));

        var ids = await source.QueryIdsAsync(DemoAdo.Project, new DateTimeOffset(2026, 10, 20, 10, 15, 0, TimeSpan.Zero));

        Assert.Equal(new[] { 1234, 1240 }, ids);
        Assert.Contains("timePrecision=true", handler.Requests.Single(), StringComparison.Ordinal);
        var query = JsonDocument.Parse(handler.Bodies.Single()!).RootElement.GetProperty("query").GetString();
        Assert.Contains("[System.ChangedDate] >= '2026-10-20T10:15:00.000Z'", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Work_items_are_read_with_relations_and_description()
    {
        var handler = DemoAdo.Server();
        var source = new AdoWorkItemSource(DemoAdo.Client(handler));

        var outcomes = await source.GetWorkItemsAsync(DemoAdo.Project, [1234, 1240]);

        Assert.Equal(2, outcomes.Count);
        var bug = outcomes[0].Record!;
        Assert.Equal("Resolved", bug.State);
        Assert.Equal("<div>Retries forever</div>", bug.Description);
        Assert.Equal("Demo\\Payments", bug.AreaPath);
        Assert.Equal(5, bug.Relations.Count);
        var body = JsonDocument.Parse(handler.Bodies.Single()!).RootElement;
        Assert.Equal("Relations", body.GetProperty("$expand").GetString());
        Assert.Equal("Omit", body.GetProperty("errorPolicy").GetString());
    }

    [Fact]
    public async Task A_deleted_work_item_is_reported_instead_of_guessed()
    {
        var handler = new FakeAdoHandler().Json(HttpMethod.Post, "/_apis/wit/workitemsbatch", "workitems-batch-with-deleted.json");

        var outcomes = await new AdoWorkItemSource(DemoAdo.Client(handler)).GetWorkItemsAsync(DemoAdo.Project, [1, 1240]);

        Assert.False(outcomes[0].IsSynced);
        Assert.Contains("deleted", outcomes[0].NotSyncedReason, StringComparison.Ordinal);
        Assert.True(outcomes[1].IsSynced);
    }

    [Fact]
    public async Task Revisions_are_read_oldest_first()
    {
        var revisions = await new AdoWorkItemSource(DemoAdo.Client(DemoAdo.Server())).GetRevisionsAsync(DemoAdo.Project, 1234);

        Assert.Equal(new[] { 1, 7 }, revisions.Select(revision => revision.Revision));
        Assert.Equal("Fixed", revisions[1].Reason);
    }

    [Fact]
    public async Task Disabled_repositories_are_skipped()
    {
        var repositories = await new AdoRepositorySource(DemoAdo.Client(DemoAdo.Server())).GetRepositoriesAsync(DemoAdo.Project);

        Assert.Equal("payments-service", Assert.Single(repositories).Name);
    }

    [Fact]
    public async Task Pull_requests_are_read_and_enriched_with_their_stated_links()
    {
        var source = new AdoRepositorySource(DemoAdo.Client(DemoAdo.Server()));

        var pullRequests = await source.GetPullRequestsAsync(DemoAdo.Project, DemoAdo.Repository);
        var completed = await source.EnrichAsync(DemoAdo.Project, DemoAdo.Repository, pullRequests[0]);

        Assert.Equal(2, pullRequests.Count);
        Assert.Equal(DemoAdo.MergeCommit, completed.MergeCommitId);
        Assert.Equal(new[] { 1234 }, completed.WorkItemIds);
        Assert.Equal(new[] { DemoAdo.Commit }, completed.CommitIds);
        Assert.Equal("user-guid-1", completed.CreatedById);
        Assert.Null(pullRequests[1].MergeCommitId);
        Assert.Null(pullRequests[1].ClosedDate);
    }

    [Fact]
    public async Task Commits_are_read_with_full_messages_and_changed_files()
    {
        var source = new AdoCommitSource(DemoAdo.Client(DemoAdo.Server()));

        var commits = await source.GetCommitsAsync(DemoAdo.Project, DemoAdo.Repository, null);
        var truncated = commits.Single(commit => commit.CommitId == DemoAdo.Commit);
        var enriched = await source.EnrichAsync(DemoAdo.Project, DemoAdo.Repository, truncated);

        Assert.Equal(3, commits.Count);
        Assert.True(truncated.CommentTruncated);
        Assert.False(enriched.CommentTruncated);
        Assert.Contains("AB#1240", enriched.Comment, StringComparison.Ordinal);
        Assert.Equal("/src/Payments/RetryPolicy.cs", Assert.Single(enriched.ChangedFiles).Path);
    }

    [Fact]
    public async Task Builds_are_read_with_their_pull_request_and_artifacts()
    {
        var source = new AdoBuildSource(DemoAdo.Client(DemoAdo.Server()));

        var build = Assert.Single(await source.GetBuildsAsync(DemoAdo.Project, null, []));
        var enriched = await source.EnrichAsync(DemoAdo.Project, build);

        Assert.Equal(77, build.PullRequestNumber);
        Assert.Equal("failed", build.Result);
        Assert.Equal(DemoAdo.MergeCommit, build.SourceVersion);
        Assert.Equal("drop", Assert.Single(enriched.Artifacts).Name);
    }

    [Fact]
    public async Task The_pipeline_filter_is_sent_with_the_builds_query()
    {
        var handler = DemoAdo.Server();

        await new AdoBuildSource(DemoAdo.Client(handler)).GetBuildsAsync(DemoAdo.Project, null, [12, 13]);

        Assert.Contains("definitions=12%2C13", handler.Requests.Single(), StringComparison.Ordinal);
    }
}
