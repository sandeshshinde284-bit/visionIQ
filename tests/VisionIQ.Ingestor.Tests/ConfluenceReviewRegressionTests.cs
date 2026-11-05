using VisionIQ.Ingestor.Confluence;

namespace VisionIQ.Ingestor.Tests;

/// <summary>Regression tests for the LRN-01..07 integration review (R2-05, R2-06).</summary>
public sealed class ConfluenceReviewRegressionTests
{
    private static ConfluenceHttp Http() =>
        new(new HttpClient(new FakeAdoHandler()), DemoConfluence.CloudOptions());

    [Theory]
    [InlineData("/wiki/api/v2/spaces/1/pages?cursor=abc", "https://contoso.atlassian.net/wiki/api/v2/spaces/1/pages?cursor=abc")]
    [InlineData("https://contoso.atlassian.net/wiki/api/v2/pages?cursor=x", "https://contoso.atlassian.net/wiki/api/v2/pages?cursor=x")]
    [InlineData("https://CONTOSO.atlassian.net/wiki/api/v2/pages?cursor=x", "https://contoso.atlassian.net/wiki/api/v2/pages?cursor=x")]
    public void Paging_links_on_the_configured_site_are_followed(string link, string expected)
    {
        Assert.Equal(new Uri(expected), Http().Link(link));
    }

    [Theory]
    [InlineData("https://attacker.example/wiki/api/v2/pages?cursor=x")]
    [InlineData("http://contoso.atlassian.net/wiki/api/v2/pages?cursor=x")]
    [InlineData("https://contoso.atlassian.net:8443/wiki/api/v2/pages")]
    [InlineData("//attacker.example/wiki/api/v2/pages")]
    public void Paging_links_off_the_configured_site_are_refused_so_the_token_is_never_sent(string link)
    {
        // R2-06: every request carries the Confluence credential.
        Assert.Throws<InvalidOperationException>(() => Http().Link(link));
    }

    [Fact]
    public void A_header_row_table_before_the_properties_table_does_not_hide_the_decision_status()
    {
        // R2-05: the lazy cell pattern used to run across </th></tr></table> and produce the key
        // "Name</th><th>Owner</th>...<th>Status", losing Status (and template decision detection).
        const string storage =
            "<table><tbody><tr><th>Name</th><th>Owner</th></tr><tr><td>Retry</td><td>Team A</td></tr></tbody></table>" +
            "<table><tbody><tr><th>Status</th><td>Accepted</td></tr><tr><th>Outcome</th><td>Cap retries at 3</td></tr></tbody></table>";

        var page = ConfluenceText.Normalize(storage);

        Assert.Equal("Accepted", page.Properties["Status"]);
        Assert.Equal("Cap retries at 3", page.Properties["Outcome"]);
        Assert.All(page.Properties.Keys, key => Assert.DoesNotContain("<", key, StringComparison.Ordinal));
    }
}
