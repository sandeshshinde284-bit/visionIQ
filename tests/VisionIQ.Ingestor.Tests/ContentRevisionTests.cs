using VisionIQ.Api.Features.Evidence;
using VisionIQ.Api.Features.TicketFixes;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Cli;
using VisionIQ.Ingestor.Confluence;

namespace VisionIQ.Ingestor.Tests;

/// <summary>
/// B-9: re-syncing a source item whose stored fields changed without a new native revision must
/// create a new revision, not an immutable-content conflict that fails the stream on every run.
/// </summary>
public sealed class ContentRevisionTests
{
    private static readonly Uri Organization = new("https://dev.azure.com/contoso");

    /// <summary>Same rule as PostgresEvidenceStore: a revision ID must never change its content.</summary>
    private sealed class ImmutableStore : IVersionedEvidenceStore
    {
        private readonly Dictionary<(string Item, string Revision), string> rows = new();

        public List<EvidenceImportResult> Results { get; } = [];

        public Task<EvidenceImportResult> SaveAsync(ArchivedEvidence evidence, CancellationToken cancellationToken = default)
        {
            var id = (evidence.ItemKey, evidence.Envelope.ProviderRevisionId);
            var status = rows.TryGetValue(id, out var existing)
                ? existing == evidence.ContentSha256 ? EvidenceImportStatus.Unchanged : EvidenceImportStatus.Conflict
                : EvidenceImportStatus.Created;
            rows.TryAdd(id, evidence.ContentSha256);
            var result = new EvidenceImportResult(evidence.ItemKey, evidence.Envelope.ProviderRevisionId, status);
            Results.Add(result);
            return Task.FromResult(result);
        }
    }

    private sealed class Archive : IRawEvidenceArchive
    {
        public Task<string> StoreAsync(string itemKey, string revisionId, byte[] rawJson, string contentSha256, CancellationToken cancellationToken = default) =>
            Task.FromResult($"gs://test/{itemKey}/{contentSha256}");
    }

    private sealed class Links : ITicketFixLinkStore
    {
        public List<TicketFixClaim> Claims { get; } = [];

        public Task<string> UpsertSourceAsync(TicketFixClaim claim, CancellationToken cancellationToken = default)
        {
            Claims.Add(claim);
            return Task.FromResult(claim.AssociationKey());
        }

        public Task<int> RecordRevertAsync(string sourceInstance, string projectId, string repositoryId, string originalCommitSha, string revertCommitSha, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private static CanonicalEvidenceSink Sink(ImmutableStore store) =>
        new(new EvidenceIngestionService(new Archive(), store), Organization, "contoso", new Uri("https://contoso.atlassian.net/wiki"));

    private static PullRequestRecord ActivePr(params string[] commits) =>
        new(42, "repo-id", "repo", "Project-A", "active", "Fix", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero))
        {
            WorkItemIds = [123],
            CommitIds = commits,
        };

    [Fact]
    public async Task An_active_pr_with_a_new_commit_becomes_a_new_revision_not_a_conflict()
    {
        var store = new ImmutableStore();
        var sink = Sink(store);

        await sink.WriteAsync("pull-requests", "contoso/ado/Project-A/repo/pr/42", ActivePr(new string('a', 40)));
        await sink.WriteAsync("pull-requests", "contoso/ado/Project-A/repo/pr/42", ActivePr(new string('a', 40), new string('b', 40)));

        Assert.Equal(new[] { EvidenceImportStatus.Created, EvidenceImportStatus.Created }, store.Results.Select(r => r.Status));
        Assert.NotEqual(store.Results[0].ProviderRevisionId, store.Results[1].ProviderRevisionId);
        Assert.All(store.Results, r => Assert.StartsWith("active@", r.ProviderRevisionId));
    }

    [Fact]
    public async Task Identical_content_is_still_idempotent()
    {
        var store = new ImmutableStore();
        var sink = Sink(store);

        await sink.WriteAsync("pull-requests", "contoso/ado/Project-A/repo/pr/42", ActivePr(new string('a', 40)));
        await sink.WriteAsync("pull-requests", "contoso/ado/Project-A/repo/pr/42", ActivePr(new string('a', 40)));

        Assert.Equal(new[] { EvidenceImportStatus.Created, EvidenceImportStatus.Unchanged }, store.Results.Select(r => r.Status));
    }

    [Fact]
    public async Task A_confluence_permission_change_without_a_new_version_is_a_new_revision()
    {
        var store = new ImmutableStore();
        var sink = Sink(store);
        ConfluencePageSnapshot Page(bool restricted) => new("contoso.atlassian.net", "ENG", "1003", 4, "Design", DateTimeOffset.UnixEpoch)
        {
            Acl = new ConfluencePageAcl("1003", "ENG", true, restricted ? [new RestrictionLevel("1003", ["acc-1"], [])] : []),
        };

        await sink.WriteAsync("confluence-pages", "contoso.atlassian.net/confluence/ENG/page/1003@4", Page(false));
        await sink.WriteAsync("confluence-pages", "contoso.atlassian.net/confluence/ENG/page/1003@4", Page(true));

        Assert.DoesNotContain(store.Results, r => r.Status == EvidenceImportStatus.Conflict);
        Assert.All(store.Results, r => Assert.StartsWith("4#", r.ProviderRevisionId));
    }

    [Fact]
    public async Task A_changed_environment_classification_is_a_new_revision_and_kept_in_metadata()
    {
        // Rules can change without a new ADO revision; that must not be an immutable-content conflict.
        var store = new ImmutableStore();
        var sink = Sink(store);
        WorkItemRecord Bug(string environment, string reason) =>
            new WorkItemRecord(7, 3, "Bug", "Active", "t", [], DateTimeOffset.UnixEpoch) { Project = "Project-A" }
                .WithEnvironment(new VisionIQ.Ingestor.Defects.EnvironmentClassification(
                    environment, environment == "UAT" ? "verified" : "unknown", "Staging", "field:Custom.Env", reason, "env-rules-abc", []));

        await sink.WriteAsync("work-items", "contoso/ado/Project-A/work-item/7", Bug("UNKNOWN", "unmapped_value"));
        await sink.WriteAsync("work-items", "contoso/ado/Project-A/work-item/7", Bug("UAT", "matched"));

        Assert.Equal(new[] { EvidenceImportStatus.Created, EvidenceImportStatus.Created }, store.Results.Select(r => r.Status));
        Assert.All(store.Results, r => Assert.StartsWith("3#", r.ProviderRevisionId));
    }

    [Fact]
    public async Task The_projection_references_exactly_the_stored_revision()
    {
        var store = new ImmutableStore();
        var links = new Links();
        var sink = new TicketFixProjectionSink(Sink(store), links, Organization);

        await sink.WriteAsync("pull-requests", "contoso/ado/Project-A/repo/pr/42", ActivePr(new string('a', 40)));

        var stored = Assert.Single(store.Results);
        var reference = Assert.Single(Assert.Single(links.Claims).SourceEvidenceRefs);
        Assert.Equal("evidence:" + stored.ItemKey + ":" + stored.ProviderRevisionId, reference);
    }
}
