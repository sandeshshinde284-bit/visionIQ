using VisionIQ.Api.Features.BuildLinks;
using VisionIQ.Api.Features.Evidence;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Cli;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Tests;

/// <summary>LRN-07: a finished build links to the commit it built and the PR that triggered it.</summary>
public sealed class BuildLinkProjectionTests
{
    private const string Instance = "https://dev.azure.com/contoso";
    private static readonly string Sha = new('a', 40);
    private static readonly DateTimeOffset Finished = new(2026, 4, 5, 6, 7, 8, TimeSpan.Zero);

    private static BuildRecord Build(string? sha = null, int? pr = null, string? repo = "repo-1", DateTimeOffset? finished = null) =>
        new(91, "Payments", "20260405.1", 3, "ci", "completed", finished) { SourceVersion = sha, PullRequestNumber = pr, RepositoryId = repo, Result = "failed" };

    [Fact]
    public void A_finished_build_links_its_commit_and_pull_request()
    {
        var link = BuildLinkProjectionSink.Link(Instance, Build(Sha, 42, finished: Finished))!;

        Assert.Equal(("Payments", "91", "repo-1", Sha, "42", Finished), (link.ProjectId, link.BuildId, link.RepositoryId, link.CommitSha, link.PullRequestId, link.SourceAssertedAt));
    }

    [Fact]
    public void Builds_that_cannot_be_cited_exactly_or_state_nothing_record_no_link()
    {
        Assert.Null(BuildLinkProjectionSink.Link(Instance, Build(Sha, 42, finished: null)));          // still running
        Assert.Null(BuildLinkProjectionSink.Link(Instance, Build(Sha, 42, repo: null, finished: Finished))); // no repository
        Assert.Null(BuildLinkProjectionSink.Link(Instance, Build(finished: Finished)));                // no commit, no PR
        Assert.Null(BuildLinkProjectionSink.Link(Instance, Build("abc123", finished: Finished)));      // short SHA, no PR
        Assert.Equal("42", BuildLinkProjectionSink.Link(Instance, Build("abc123", 42, finished: Finished))!.PullRequestId);
    }

    private sealed class RecordingStore : IVersionedEvidenceStore
    {
        public List<ArchivedEvidence> Saved { get; } = [];

        public Task<EvidenceImportResult> SaveAsync(ArchivedEvidence evidence, CancellationToken cancellationToken = default)
        {
            Saved.Add(evidence);
            return Task.FromResult(new EvidenceImportResult(evidence.ItemKey, evidence.Envelope.ProviderRevisionId, EvidenceImportStatus.Created));
        }
    }

    private sealed class Archive : IRawEvidenceArchive
    {
        public Task<string> StoreAsync(string itemKey, string revisionId, byte[] rawJson, string contentSha256, CancellationToken cancellationToken = default) =>
            Task.FromResult("gs://test/" + itemKey);
    }

    private sealed class Links : IBuildLinkStore
    {
        public List<BuildLink> Upserted { get; } = [];

        public Task UpsertAsync(BuildLink link, CancellationToken cancellationToken = default)
        {
            Upserted.Add(link);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task The_link_cites_exactly_the_build_evidence_revision_that_was_stored()
    {
        var store = new RecordingStore();
        var links = new Links();
        var canonical = new CanonicalEvidenceSink(new EvidenceIngestionService(new Archive(), store), new Uri(Instance), "contoso", null);
        var sink = new BuildLinkProjectionSink(canonical, links, new Uri(Instance));

        await sink.WriteAsync("builds", "contoso/ado/Payments/build/91", Build(Sha, 42, finished: Finished));

        var stored = Assert.Single(store.Saved);
        Assert.Equal("evidence:" + stored.ItemKey + ":" + stored.Envelope.ProviderRevisionId, Assert.Single(links.Upserted).EvidenceRef);
    }
}
