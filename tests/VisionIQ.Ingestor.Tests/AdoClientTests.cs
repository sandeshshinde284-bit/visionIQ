using System.Net;
using System.Text;
using VisionIQ.Ingestor.Ado;

namespace VisionIQ.Ingestor.Tests;

public sealed class AdoClientTests
{
    [Fact]
    public async Task Requests_carry_basic_auth_and_the_api_version()
    {
        var handler = new FakeAdoHandler().Json(HttpMethod.Get, "/_apis/projects", "projects.json");
        var client = DemoAdo.Client(handler);

        using var response = await client.GetAsync(client.BuildUri(null, "_apis/projects"));

        var expected = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":test-token-not-real"));
        Assert.Equal(expected, handler.Authorizations.Single());
        Assert.Contains("api-version=7.1", handler.Requests.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Throttling_is_retried_after_the_Retry_After_delay()
    {
        var calls = 0;
        var handler = new FakeAdoHandler().On(HttpMethod.Get, "/_apis/projects", _ =>
            ++calls == 1
                ? FakeAdoHandler.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(7))
                : FakeAdoHandler.Ok(Fixture.Read("projects.json")));
        var delays = new List<TimeSpan>();
        var client = DemoAdo.Client(handler, delays: delays);

        using var response = await client.GetAsync(client.BuildUri(null, "_apis/projects"));

        Assert.Equal(2, calls);
        Assert.Equal(TimeSpan.FromSeconds(7), delays.Single());
    }

    [Fact]
    public async Task Access_denied_is_reported_without_retrying_and_without_the_token()
    {
        var handler = new FakeAdoHandler().On(HttpMethod.Get, "/_apis/projects", _ => FakeAdoHandler.Status(HttpStatusCode.Unauthorized));
        var client = DemoAdo.Client(handler);

        var error = await Assert.ThrowsAsync<AdoSourceException>(() => client.GetAsync(client.BuildUri(null, "_apis/projects")));

        Assert.True(error.IsAccessDenied);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("test-token-not-real", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sign_in_page_means_the_token_is_invalid()
    {
        var handler = new FakeAdoHandler().On(HttpMethod.Get, "/_apis/projects", _ =>
            new HttpResponseMessage(HttpStatusCode.NonAuthoritativeInformation) { Content = new StringContent("<html>Sign in</html>") });
        var client = DemoAdo.Client(handler);

        var error = await Assert.ThrowsAsync<AdoSourceException>(() => client.GetAsync(client.BuildUri(null, "_apis/projects")));

        Assert.True(error.IsAccessDenied);
        Assert.Contains("sign-in page", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Persistent_server_errors_fail_after_the_retry_limit()
    {
        var handler = new FakeAdoHandler().On(HttpMethod.Get, "/_apis/projects", _ => FakeAdoHandler.Status(HttpStatusCode.ServiceUnavailable));
        var delays = new List<TimeSpan>();
        var client = DemoAdo.Client(handler, delays: delays);

        var error = await Assert.ThrowsAsync<AdoSourceException>(() => client.GetAsync(client.BuildUri(null, "_apis/projects")));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public void Project_names_are_escaped_in_addresses()
    {
        var client = DemoAdo.Client(new FakeAdoHandler());

        var uri = client.BuildUri("VisionIQ Demo", "_apis/wit/wiql", [new("timePrecision", "true")]);

        Assert.Contains("/contoso/VisionIQ%20Demo/_apis/wit/wiql?timePrecision=true&api-version=7.1", uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_continuation_token_header_is_returned()
    {
        var handler = new FakeAdoHandler().On(HttpMethod.Get, "/_apis/projects", _ => FakeAdoHandler.Ok("{\"value\":[]}", "next-page"));
        var client = DemoAdo.Client(handler);

        using var response = await client.GetAsync(client.BuildUri(null, "_apis/projects"));

        Assert.Equal("next-page", response.ContinuationToken);
    }
}
