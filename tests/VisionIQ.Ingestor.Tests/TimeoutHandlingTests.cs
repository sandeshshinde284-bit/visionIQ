using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Confluence;

namespace VisionIQ.Ingestor.Tests;

/// <summary>B-2/B-3: an HttpClient timeout is a transient source failure, never an unhandled crash.</summary>
public sealed class TimeoutHandlingTests
{
    private static HttpResponseMessage TimeOut(HttpRequestMessage _) =>
        throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException());

    [Fact]
    public async Task Ado_timeout_is_retried_then_succeeds()
    {
        var calls = 0;
        var handler = new FakeAdoHandler().On(HttpMethod.Get, "/_apis/projects", request =>
            ++calls < 3 ? TimeOut(request) : FakeAdoHandler.Ok(Fixture.Read("projects.json")));
        var delays = new List<TimeSpan>();
        var client = DemoAdo.Client(handler, delays: delays);

        using var response = await client.GetAsync(client.BuildUri(null, "_apis/projects"));

        Assert.Equal(3, calls);
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task Ado_timeout_after_all_retries_becomes_a_source_error()
    {
        var handler = new FakeAdoHandler().On(HttpMethod.Get, "/_apis/projects", TimeOut);
        var client = DemoAdo.Client(handler, delays: new List<TimeSpan>());

        var error = await Assert.ThrowsAsync<AdoSourceException>(() => client.GetAsync(client.BuildUri(null, "_apis/projects")));

        Assert.Contains("did not respond in time", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ado_caller_cancellation_is_still_cancellation()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        var handler = new FakeAdoHandler().Json(HttpMethod.Get, "/_apis/projects", "projects.json");
        var client = DemoAdo.Client(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync(client.BuildUri(null, "_apis/projects"), cancel.Token));
    }

    [Fact]
    public async Task Confluence_timeout_after_all_retries_becomes_a_source_error()
    {
        var handler = new FakeAdoHandler().On(HttpMethod.Get, "/wiki/api/v2/spaces", TimeOut);
        var source = DemoConfluence.Source(handler, DemoConfluence.CloudOptions());

        await Assert.ThrowsAsync<ConfluenceSourceException>(() => source.GetPagesAsync("ENG"));
    }
}
