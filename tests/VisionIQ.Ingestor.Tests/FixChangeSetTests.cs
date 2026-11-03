using VisionIQ.Api.Features.Evidence;
using VisionIQ.Api.Features.Lessons;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Cli;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Tests;

/// <summary>LRN-10: each fix's CHANGED files are recorded after its evidence; unknown is never "no files".</summary>
public sealed class FixChangeSetTests
{
    private sealed class Store : IFixChangeSetStore
    {
        public List<FixChangeSet> Saved { get; } = [];

        public Task UpsertAsync(FixChangeSet changeSet, CancellationToken cancellationToken = default)
        {
            Saved.Add(changeSet);
            return Task.CompletedTask;
        }
    }

    private sealed class Evidence : ICanonicalEvidenceSink
    {
        public List<string> Streams { get; } = [];

        public Task WriteAsync(string stream, string key, object record, CancellationToken cancellationToken = default)
        {
            Streams.Add(stream);
            return Task.CompletedTask;
        }
    }

    private static (FixChangeSetProjectionSink Sink, Store Store, Evidence Evidence) Build()
    {
        var store = new Store();
        var evidence = new Evidence();
        return (new FixChangeSetProjectionSink(evidence, store, new Uri("https://dev.azure.com/contoso/")), store, evidence);
    }

    [Fact]
    public async Task A_prs_changed_files_are_recorded_after_its_evidence_with_matching_provenance()
    {
        var (sink, store, evidence) = Build();
        var pr = new PullRequestRecord(42, "repo-1", "shop", "Payments", "completed", "t", DateTimeOffset.UnixEpoch)
        {
            ChangedFiles = [new("/src/B.cs", "edit"), new("/src/A.cs", "add"), new("/src/A.cs", "edit")],
        };

        await sink.WriteAsync("pull-requests", "k", pr);

        Assert.Equal(new[] { "pull-requests" }, evidence.Streams);
        var saved = Assert.Single(store.Saved);
        Assert.Equal(("pull_request", "42", "repo-1", "Payments"), (saved.FixKind, saved.FixId, saved.RepositoryId, saved.ProjectId));
        Assert.Equal(new[] { "/src/A.cs", "/src/B.cs" }, saved.Paths); // distinct and sorted
        Assert.Equal(new EvidenceIdentity("azure_devops", "https://dev.azure.com/contoso", "Payments", "repo-1", "pull_request", "42").ItemKey(),
            saved.ProvenanceItemKey);
    }

    [Fact]
    public async Task A_pr_whose_changes_were_unavailable_records_nothing()
    {
        var (sink, store, _) = Build();

        await sink.WriteAsync("pull-requests", "k", new PullRequestRecord(42, "repo-1", "shop", "Payments", "active", "t", DateTimeOffset.UnixEpoch));

        Assert.Empty(store.Saved); // unknown is never stored as "changed nothing"
    }

    [Fact]
    public async Task A_commits_changes_keep_the_truncation_flag_and_other_records_are_ignored()
    {
        var (sink, store, _) = Build();
        var commit = new CommitRecord("ABCDEF0123456789ABCDEF0123456789ABCDEF01", "repo-1", "Payments", DateTimeOffset.UnixEpoch, "fix")
        {
            ChangedFiles = [new ChangedFile("/src/Retry.cs", "edit")],
            ChangedFilesTruncated = true,
        };

        await sink.WriteAsync("commits", "k", commit);
        await sink.WriteAsync("work-items", "k", new WorkItemRecord(1, 1, "Bug", "New", "t", [], DateTimeOffset.UnixEpoch));

        var saved = Assert.Single(store.Saved);
        Assert.Equal(("commit", "abcdef0123456789abcdef0123456789abcdef01", true), (saved.FixKind, saved.FixId, saved.Truncated));
    }
}
