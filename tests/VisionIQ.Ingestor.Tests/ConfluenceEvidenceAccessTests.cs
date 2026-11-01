using VisionIQ.Api.Features.Evidence;
using VisionIQ.Ingestor.Cli;
using VisionIQ.Ingestor.Confluence;

namespace VisionIQ.Ingestor.Tests;

/// <summary>LRN-02: page evidence names its space for the live check; ACL snapshots are never readable.</summary>
public sealed class ConfluenceEvidenceAccessTests
{
    private sealed class Recording : IVersionedEvidenceStore
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

    [Fact]
    public async Task Pages_carry_their_space_principal_and_acl_snapshots_stay_hidden()
    {
        var store = new Recording();
        var sink = new CanonicalEvidenceSink(new EvidenceIngestionService(new Archive(), store),
            new Uri("https://dev.azure.com/contoso"), "contoso", new Uri("https://contoso.atlassian.net"));
        var acl = new ConfluencePageAcl("1001", "ENG", true, []);

        await sink.WriteAsync("confluence-pages", "contoso.atlassian.net/confluence/ENG/page/1001@3",
            new ConfluencePageSnapshot("contoso.atlassian.net", "ENG", "1001", 3, "Design", DateTimeOffset.UnixEpoch) { Acl = acl });
        await sink.WriteAsync("confluence-acl", "contoso.atlassian.net/confluence/ENG/page/1001#fp", acl);

        var page = Assert.Single(store.Saved, s => s.Envelope.Identity.Kind == "design_page");
        Assert.True(page.Envelope.Access.Verified);
        Assert.Equal(new[] { "confluence-space:https://contoso.atlassian.net/ENG" }, page.Envelope.Access.PrincipalIds);
        Assert.False(Assert.Single(store.Saved, s => s.Envelope.Identity.Kind == "confluence_acl").Envelope.Access.Verified);
    }
}
