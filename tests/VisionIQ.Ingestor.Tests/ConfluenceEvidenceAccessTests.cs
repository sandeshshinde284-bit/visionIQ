using VisionIQ.Api.Features.Evidence;
using VisionIQ.Ingestor.Cli;
using VisionIQ.Ingestor.Confluence;

namespace VisionIQ.Ingestor.Tests;

/// <summary>LRN-02: page evidence names its space for the (currently removed) live check.</summary>
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
    public async Task Pages_carry_their_space_principal()
    {
        var store = new Recording();
        var sink = new CanonicalEvidenceSink(new EvidenceIngestionService(new Archive(), store),
            new Uri("https://dev.azure.com/contoso"), "contoso", new Uri("https://contoso.atlassian.net"));

        await sink.WriteAsync("confluence-pages", "contoso.atlassian.net/confluence/ENG/page/1001@3",
            new ConfluencePageSnapshot("contoso.atlassian.net", "ENG", "1001", 3, "Design", DateTimeOffset.UnixEpoch));

        var page = Assert.Single(store.Saved, s => s.Envelope.Identity.Kind == "design_page");
        Assert.True(page.Envelope.Access.Verified);
        Assert.Equal(new[] { "confluence-space:https://contoso.atlassian.net/ENG" }, page.Envelope.Access.PrincipalIds);
    }
}
