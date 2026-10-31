using VisionIQ.Ingestor.Confluence;

namespace VisionIQ.Ingestor.Tests;

/// <summary>M-15: labels and versions beyond the first page are read.</summary>
public sealed class ConfluenceEnrichPagingTests
{
    private static HttpResponseMessage Page(string items, string? next) =>
        FakeAdoHandler.Ok("{\"results\":[" + items + "]" + (next is null ? string.Empty : ",\"_links\":{\"next\":\"" + next + "\"}") + "}");

    [Fact]
    public async Task Labels_and_versions_on_later_pages_are_included()
    {
        var handler = new FakeAdoHandler()
            .On(HttpMethod.Get, "/labels", request => request.RequestUri!.Query.Contains("cursor=2", StringComparison.Ordinal)
                ? Page("{\"name\":\"adr\"}", null)
                : Page("{\"name\":\"payments\"}", "/wiki/api/v2/pages/1001/labels?cursor=2"))
            .On(HttpMethod.Get, "/versions", request => request.RequestUri!.Query.Contains("cursor=2", StringComparison.Ordinal)
                ? Page("{\"number\":1,\"createdAt\":\"2026-01-01T00:00:00Z\"}", null)
                : Page("{\"number\":3,\"createdAt\":\"2026-03-01T00:00:00Z\"},{\"number\":2,\"createdAt\":\"2026-02-01T00:00:00Z\"}", "/wiki/api/v2/pages/1001/versions?cursor=2"));
        var source = DemoConfluence.Source(handler, DemoConfluence.CloudOptions());

        var enriched = await source.EnrichAsync(new ConfluencePage("1001", "ENG", "Design", 3, DateTimeOffset.UnixEpoch));

        Assert.Equal(new[] { "payments", "adr" }, enriched.Labels);
        Assert.Equal(new[] { 1, 2, 3 }, enriched.Versions.Select(version => version.Number));
    }
}
