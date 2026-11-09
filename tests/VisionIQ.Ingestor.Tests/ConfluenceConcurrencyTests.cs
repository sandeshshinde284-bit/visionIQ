using System.Text.RegularExpressions;
using VisionIQ.Ingestor.Confluence;

namespace VisionIQ.Ingestor.Tests;

/// <summary>
/// R2-02: the per-page restriction fetch (Confluence Cloud) now runs with bounded concurrency instead
/// of one page at a time. These tests prove concurrency never scrambles which restriction belongs to
/// which page, that progress is actually logged, and that a bad concurrency setting can't crash it.
/// </summary>
public sealed partial class ConfluenceConcurrencyTests
{
    private const int PageCount = 40;

    [GeneratedRegex(@"content/(\d+)/restriction")]
    private static partial Regex PageIdInPath();

    private static ConfluenceOptions Options(int concurrency) => new()
    {
        BaseUrl = new Uri("https://contoso.atlassian.net/wiki"),
        SpaceKeys = ["ENG"],
        Deployment = ConfluenceDeployment.Cloud,
        UserEmail = "ci@contoso.com",
        ApiToken = "conf-token-not-real",
        MaxRetries = 1,
        RestrictionFetchConcurrency = concurrency,
    };

    private static FakeAdoHandler ManyPagesServer()
    {
        var handler = new FakeAdoHandler();
        handler.Json(HttpMethod.Get, "/wiki/api/v2/spaces", "conf-cloud-spaces.json");
        handler.On(HttpMethod.Get, "/spaces/98304/pages", _ => FakeAdoHandler.Ok(PagesJson()));
        handler.On(HttpMethod.Get, "/restriction/byOperation/read", request =>
        {
            var match = PageIdInPath().Match(request.RequestUri!.AbsolutePath);
            var id = match.Success ? match.Groups[1].Value : "unknown";
            return FakeAdoHandler.Ok(RestrictionJson(id));
        });
        return handler;
    }

    private static string PagesJson()
    {
        var items = Enumerable.Range(1, PageCount).Select(id =>
            $"{{\"id\":\"{id}\",\"title\":\"Page {id}\",\"version\":{{\"number\":1,\"createdAt\":\"2026-01-01T00:00:00Z\"}}," +
            "\"body\":{\"storage\":{\"value\":\"\"}},\"_links\":{\"webui\":\"/x/" + id + "\"}}");
        return "{\"results\":[" + string.Join(",", items) + "],\"_links\":{}}";
    }

    /// <summary>Each page's restriction names its own page ID, so a mixed-up index would fail this.</summary>
    private static string RestrictionJson(string pageId) =>
        "{\"restrictions\":{\"user\":{\"results\":[{\"accountId\":\"user-" + pageId + "\"}],\"size\":1,\"limit\":200}," +
        "\"group\":{\"results\":[],\"size\":0,\"limit\":200}}}";

    [Fact]
    public async Task Concurrent_restriction_fetches_never_mix_up_which_page_they_belong_to()
    {
        var handler = ManyPagesServer();
        var options = Options(concurrency: 8);
        var source = ConfluenceSources.Create(new HttpClient(handler), options, (_, _) => Task.CompletedTask);

        var pages = await source.GetPagesAsync("ENG");

        Assert.Equal(PageCount, pages.Count);
        foreach (var page in pages)
        {
            Assert.Equal(["user-" + page.Id], page.ReadRestriction.Users);
        }
    }

    [Fact]
    public async Task Progress_is_logged_for_discovery_and_restriction_fetch()
    {
        var handler = ManyPagesServer();
        var log = new LogCapture();
        var options = Options(concurrency: 4);
        var source = ConfluenceSources.Create(new HttpClient(handler), options, (_, _) => Task.CompletedTask, log);

        await source.GetPagesAsync("ENG");

        Assert.Contains(log.Lines, l => l.Message.Contains($"discovered {PageCount} page", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Message.Contains($"fetched restrictions for {PageCount}/{PageCount}", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_zero_or_negative_concurrency_setting_falls_back_instead_of_crashing(int configured)
    {
        var handler = ManyPagesServer();
        var options = Options(configured);
        var source = ConfluenceSources.Create(new HttpClient(handler), options, (_, _) => Task.CompletedTask);

        var pages = await source.GetPagesAsync("ENG");

        Assert.Equal(PageCount, pages.Count);
    }
}
