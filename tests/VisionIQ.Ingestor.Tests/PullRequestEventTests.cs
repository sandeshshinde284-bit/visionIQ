using System.Text.Json;
using VisionIQ.Api.Features.Backfill;
using VisionIQ.Api.Features.Evidence;
using VisionIQ.Api.Features.TicketFixes;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Cli;

namespace VisionIQ.Ingestor.Tests;

/// <summary>
/// LRN-12: PR created, updated, reviewed, abandoned and merged events are each stored once; late or out-of-order
/// events end in the same result; no PR event marks a root cause as verified.
/// </summary>
public sealed class PullRequestEventTests
{
    // ---- S12-01 lifecycle model -------------------------------------------------------------------

    private static PullRequestRecord Pr(string status = "active", bool draft = false, params PullRequestReviewer[] reviewers) =>
        new(42, "repo-1", "shop", "VisionIQ Demo", status, "Fix retry loop", DateTimeOffset.UnixEpoch) { IsDraft = draft, Reviewers = reviewers };

    private static PullRequestReviewer R(string id, int vote, bool required = false, bool declined = false) => new(id, vote, required, declined);

    [Fact]
    public void The_lifecycle_state_follows_status_draft_and_votes()
    {
        Assert.Equal("merged", Pr("completed").Lifecycle);
        Assert.Equal("abandoned", Pr("abandoned").Lifecycle);
        Assert.Equal("draft", Pr(draft: true).Lifecycle);
        Assert.Equal("active", Pr().Lifecycle);
        Assert.Equal("rejected", Pr("active", false, R("a", 10), R("b", -10)).Lifecycle);
        Assert.Equal("waiting_for_author", Pr("active", false, R("a", -5)).Lifecycle);
        Assert.Equal("approved", Pr("active", false, R("a", 10, required: true), R("b", 5)).Lifecycle);
        Assert.Equal("active", Pr("active", false, R("a", 10), R("lead", 0, required: true)).Lifecycle); // required reviewer pending
        Assert.Equal("approved", Pr("active", false, R("a", 10), R("x", -10, declined: true)).Lifecycle); // declined reviewers do not count
    }

    // ---- Event parsing --------------------------------------------------------------------------

    private const string EventTemplate = """
        {"id":"EVENT","eventType":"TYPE","createdDate":"2026-03-01T10:00:00Z","resource":{
          "pullRequestId":42,"status":"active","url":"https://dev.azure.com/contoso/_apis/git/repositories/repo-1/pullRequests/42",
          "repository":{"id":"repo-1","name":"shop","project":{"name":"PROJECT"}},"title":"ignored, never trusted"}}
        """;

    private static string Event(string type = "git.pullrequest.updated", string id = "evt-1", string project = "VisionIQ Demo") =>
        EventTemplate.Replace("EVENT", id, StringComparison.Ordinal).Replace("TYPE", type, StringComparison.Ordinal).Replace("PROJECT", project, StringComparison.Ordinal);

    private static ServiceHookOutcome Parse(string json, out SourceChangeNotification? change)
    {
        using var document = JsonDocument.Parse(json);
        return AdoServiceHook.Parse(document.RootElement, new Uri("https://dev.azure.com/contoso"), ["VisionIQ Demo"], "contoso", out change);
    }

    [Theory]
    [InlineData("git.pullrequest.created")]
    [InlineData("git.pullrequest.updated")]
    [InlineData("git.pullrequest.merged")]
    public void Pr_events_identify_the_pr_to_refetch(string type)
    {
        Assert.Equal(ServiceHookOutcome.Accepted, Parse(Event(type), out var change));
        Assert.Equal(("pull_request", "repo-1:42", "contoso/ado/VisionIQ Demo"), (change!.ProviderKind, change.NativeId, change.SourceId));
    }

    [Fact]
    public void A_comment_event_carries_the_pr_inside_the_resource()
    {
        const string Comment = """
            {"id":"evt-c","eventType":"ms.vss-code.git-pullrequest-comment-event","resource":{"comment":{"content":"secret review text"},
              "pullRequest":{"pullRequestId":42,"url":"https://dev.azure.com/contoso/_apis/git/repositories/repo-1/pullRequests/42",
                "repository":{"id":"repo-1","project":{"name":"VisionIQ Demo"}}}}}
            """;

        Assert.Equal(ServiceHookOutcome.Accepted, Parse(Comment, out var change));
        Assert.Equal("repo-1:42", change!.NativeId);
    }

    [Fact]
    public void Pr_events_for_other_projects_are_ignored_and_malformed_ones_invalid()
    {
        Assert.Equal(ServiceHookOutcome.Ignored, Parse(Event(project: "Secret"), out _));
        Assert.Equal(ServiceHookOutcome.Invalid, Parse("""{"id":"e","eventType":"git.pullrequest.updated","resource":{"pullRequestId":42}}""", out _));
    }

    // ---- Re-fetch, store once, replay and ordering -----------------------------------------------

    /// <summary>ADO serving one PR whose current state the test changes.</summary>
    private sealed class Ado
    {
        public string Status { get; set; } = "active";

        public string Reviewers { get; set; } = "[]";

        public string Project { get; set; } = "VisionIQ Demo";

        public FakeAdoHandler Handler() => new FakeAdoHandler()
            .On(HttpMethod.Get, "/_apis/git/repositories/repo-1/pullrequests/42", _ => FakeAdoHandler.Ok(
                "{\"pullRequestId\":42,\"status\":\"" + Status + "\",\"title\":\"Fix retry loop AB#1234\",\"creationDate\":\"2026-03-01T09:00:00Z\"," +
                (Status == "active" ? "" : "\"closedDate\":\"2026-03-02T09:00:00Z\",") +
                "\"lastMergeCommit\":{\"commitId\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}," +
                "\"repository\":{\"id\":\"repo-1\",\"name\":\"shop\",\"project\":{\"name\":\"" + Project + "\"}}," +
                "\"reviewers\":" + Reviewers + "}"))
            .On(HttpMethod.Get, "/pullRequests/42/workitems", _ => FakeAdoHandler.Ok("""{"value":[{"id":"1234"}]}"""))
            .On(HttpMethod.Get, "/pullRequests/42/commits", _ => FakeAdoHandler.Ok("""{"value":[{"commitId":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}]}"""));
    }

    /// <summary>Behaves like the evidence store: one revision per (item, revision ID); identical content is a no-op.</summary>
    private sealed class Store : IVersionedEvidenceStore
    {
        public Dictionary<(string Item, string Revision), string> Revisions { get; } = [];

        public Task<EvidenceImportResult> SaveAsync(ArchivedEvidence evidence, CancellationToken cancellationToken = default)
        {
            var key = (evidence.ItemKey, evidence.Envelope.ProviderRevisionId);
            var status = Revisions.TryAdd(key, evidence.ContentSha256) ? EvidenceImportStatus.Created : EvidenceImportStatus.Unchanged;
            return Task.FromResult(new EvidenceImportResult(evidence.ItemKey, evidence.Envelope.ProviderRevisionId, status));
        }

        public int PullRequestRevisions => Revisions.Count(pair => pair.Key.Revision.StartsWith("active", StringComparison.Ordinal)
            || pair.Key.Revision.StartsWith("completed", StringComparison.Ordinal) || pair.Key.Revision.StartsWith("abandoned", StringComparison.Ordinal));
    }

    private sealed class Archive : IRawEvidenceArchive
    {
        public Task<string> StoreAsync(string itemKey, string revisionId, byte[] rawJson, string contentSha256, CancellationToken cancellationToken = default) =>
            Task.FromResult("gs://test/" + itemKey + "/" + contentSha256);
    }

    /// <summary>Records ticket-fix claims; reviews (where "verified" lives) are not reachable from ingestion.</summary>
    private sealed class Claims : ITicketFixLinkStore
    {
        public List<TicketFixClaim> Upserted { get; } = [];

        public Task<string> UpsertSourceAsync(TicketFixClaim claim, CancellationToken cancellationToken = default)
        {
            Upserted.Add(claim);
            return Task.FromResult(claim.AssociationKey());
        }

        public Task<string> UpsertCandidateEvidenceAsync(TicketFixClaim claim, CancellationToken cancellationToken = default)
        {
            Upserted.Add(claim);
            return Task.FromResult(claim.AssociationKey());
        }

        public Task<int> RecordRevertAsync(string sourceInstance, string projectId, string repositoryId, string originalCommitSha, string revertCommitSha, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task ReconcileSourceAsync(string sourceItemKey, string sourceRevisionId, DateTimeOffset sourceRevisionTime,
            bool isCompleteSnapshot, IReadOnlyList<TicketFixClaim> claims, CancellationToken cancellationToken = default)
        {
            Upserted.AddRange(claims);
            return Task.CompletedTask;
        }
    }

    private static (AdoPullRequestChangeHandler Handler, Store Store, Claims Claims) Build(Ado ado, IReadOnlyList<string>? repositories = null)
    {
        var store = new Store();
        var claims = new Claims();
        var options = DemoAdo.Options(repositories);
        var evidence = new TicketFixProjectionSink(
            new CanonicalEvidenceSink(new EvidenceIngestionService(new Archive(), store), options.OrganizationUrl, options.Tenant),
            claims, options.OrganizationUrl);
        var source = new AdoRepositorySource(DemoAdo.Client(ado.Handler()));
        return (new AdoPullRequestChangeHandler(options, new AdoChangedPullRequestReader(source), evidence), store, claims);
    }

    private static SourceChangeNotification Change(string eventId = "evt-1") =>
        new("contoso/ado/VisionIQ Demo", eventId, "pull_request", "repo-1:42", "2026-03-01T10:00:00Z");

    [Fact]
    public async Task Created_updated_reviewed_abandoned_and_merged_states_are_each_stored_once()
    {
        var ado = new Ado();
        var (handler, store, _) = Build(ado);

        await handler.ApplyAsync(Change("created"));
        ado.Reviewers = """[{"id":"rev-1","vote":10,"isRequired":true}]""";
        await handler.ApplyAsync(Change("reviewed"));
        ado.Status = "abandoned";
        await handler.ApplyAsync(Change("abandoned"));
        ado.Status = "completed";
        await handler.ApplyAsync(Change("merged"));

        Assert.Equal(4, store.PullRequestRevisions); // one revision per distinct state, none duplicated
    }

    [Fact]
    public async Task The_same_event_replayed_twice_leaves_one_stored_result()
    {
        var (handler, store, _) = Build(new Ado());

        await handler.ApplyAsync(Change());
        await handler.ApplyAsync(Change());

        Assert.Equal(1, store.PullRequestRevisions);
    }

    [Fact]
    public async Task Late_or_out_of_order_events_end_in_the_same_result()
    {
        // In order: created, then merged.
        var inOrder = new Ado();
        var (first, ordered, _) = Build(inOrder);
        await first.ApplyAsync(Change("created"));
        inOrder.Status = "completed";
        await first.ApplyAsync(Change("merged"));

        // Out of order: the merged event first, then the late "created" event (both re-read the merged PR).
        var late = new Ado { Status = "completed" };
        var (second, reordered, _) = Build(late);
        await second.ApplyAsync(Change("merged"));
        await second.ApplyAsync(Change("created"));

        var finalInOrder = ordered.Revisions.Keys.Where(k => k.Revision.StartsWith("completed", StringComparison.Ordinal)).ToHashSet();
        var finalReordered = reordered.Revisions.Keys.Where(k => k.Revision.StartsWith("completed", StringComparison.Ordinal)).ToHashSet();
        Assert.Equal(finalInOrder, finalReordered); // the same merged state is the latest either way
        Assert.Equal(1, reordered.PullRequestRevisions); // the late event added nothing
    }

    [Fact]
    public async Task No_pr_event_marks_a_root_cause_as_verified()
    {
        var ado = new Ado { Status = "completed", Reviewers = """[{"id":"rev-1","vote":10,"isRequired":true}]""" };
        var (handler, _, claims) = Build(ado);

        await handler.ApplyAsync(Change("merged")); // merged and approved: still only a claim

        Assert.NotEmpty(claims.Upserted);
        Assert.All(claims.Upserted, claim => Assert.NotEqual(TicketFixSourceClassification.Unknown, claim.Classification));
        Assert.All(claims.Upserted, claim => Assert.True(
            claim.Classification is TicketFixSourceClassification.Explicit or TicketFixSourceClassification.Inferred,
            "ingestion only records explicit or inferred claims; 'verified' exists only as a human review"));
    }

    [Fact]
    public async Task A_pr_in_another_project_or_repository_is_refused_or_skipped()
    {
        var elsewhere = new Ado { Project = "Secret" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Build(elsewhere).Handler.ApplyAsync(Change()));

        var (handler, store, _) = Build(new Ado(), repositories: ["other-repo"]);
        await handler.ApplyAsync(Change());
        Assert.Empty(store.Revisions); // not an approved repository: acknowledged, nothing stored
    }

    [Fact]
    public async Task Reviewer_identities_are_ids_only()
    {
        var ado = new Ado { Reviewers = """[{"id":"rev-1","displayName":"Ana Lopez","uniqueName":"ana@contoso.com","vote":5}]""" };
        var source = new AdoRepositorySource(DemoAdo.Client(ado.Handler()));

        var read = await source.GetPullRequestAsync("VisionIQ Demo", "repo-1", 42);

        var reviewer = Assert.Single(read!.Value.PullRequest.Reviewers);
        Assert.Equal(new PullRequestReviewer("rev-1", 5, false, false), reviewer);
        Assert.DoesNotContain("ana@contoso.com", JsonSerializer.Serialize(read.Value.PullRequest), StringComparison.Ordinal);
    }
}
