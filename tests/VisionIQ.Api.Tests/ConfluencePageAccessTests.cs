using System.Net;
using System.Text;
using VisionIQ.Api.Features.Evidence;
using VisionIQ.Ingestor.Confluence;

namespace VisionIQ.Api.Tests;

/// <summary>LRN-02: a page is readable only when Confluence's own live permission check says so.</summary>
public sealed class ConfluencePageAccessTests
{
    private const string Site = "https://contoso.atlassian.net";

    private sealed class Confluence(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<(string Path, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.RequestUri!.AbsolutePath, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly MappedConfluenceIdentityResolver Identities = new(new Dictionary<string, string> { ["Ana@Contoso.com"] = "acc-ana" });

    private static (ConfluencePageAccessVerifier Verifier, Confluence Http) Build(
        HttpStatusCode status = HttpStatusCode.OK, string body = """{"hasPermission":true}""", ConfluenceDeployment deployment = ConfluenceDeployment.Cloud)
    {
        var handler = new Confluence(status, body);
        var http = new ConfluenceHttp(new HttpClient(handler), new ConfluenceOptions
        {
            BaseUrl = new Uri(Site), SpaceKeys = ["ENG"], Deployment = deployment, UserEmail = "svc@contoso.com", ApiToken = "test-only", MaxRetries = 0,
        }, (_, _) => Task.CompletedTask);
        return (new ConfluencePageAccessVerifier(http, Identities), handler);
    }

    private static EvidenceIdentity Page(string kind = "design_page", string space = "ENG", string id = "1001", string site = Site + "/wiki") =>
        new("confluence", site, space, null, kind, id);

    private static readonly EvidenceRequester Ana = new("ana@contoso.com", new HashSet<string>());

    [Fact]
    public async Task Confluence_saying_yes_for_this_user_allows_the_page()
    {
        var (verifier, http) = Build();

        Assert.True(await verifier.CanReadNowAsync(Page(), Ana));
        var (path, body) = Assert.Single(http.Calls);
        Assert.Equal("/wiki/rest/api/content/1001/permission/check", path);
        Assert.Contains("\"acc-ana\"", body, StringComparison.Ordinal);
        Assert.Contains("\"read\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confluence_saying_no_denies() =>
        Assert.False(await Build(body: """{"hasPermission":false}""").Verifier.CanReadNowAsync(Page(), Ana));

    [Fact]
    public async Task An_unmapped_user_is_denied_without_asking_confluence()
    {
        var (verifier, http) = Build();

        Assert.False(await verifier.CanReadNowAsync(Page(), new EvidenceRequester("eve@contoso.com", new HashSet<string>())));
        Assert.Empty(http.Calls);
    }

    [Fact]
    public async Task A_confluence_error_denies() =>
        Assert.False(await Build(HttpStatusCode.Forbidden, "{}").Verifier.CanReadNowAsync(Page(), Ana));

    [Fact]
    public async Task Data_center_pages_stay_hidden() =>
        Assert.False(await Build(deployment: ConfluenceDeployment.DataCenter).Verifier.CanReadNowAsync(Page(), Ana));

    [Theory]
    [InlineData("confluence_acl", "ENG", "1001", Site)]
    [InlineData("design_page", "SECRET", "1001", Site)]
    [InlineData("design_page", "ENG", "1001; x", Site)]
    [InlineData("design_page", "ENG", "1001", "https://other.atlassian.net")]
    public async Task Other_kinds_spaces_ids_and_sites_are_denied(string kind, string space, string id, string site) =>
        Assert.False(await Build().Verifier.CanReadNowAsync(Page(kind, space, id, site), Ana));
}

/// <summary>The same Confluence site written with or without "/wiki" must produce the same principal.</summary>
public sealed class ConfluencePrincipalTests
{
    [Theory]
    [InlineData("https://contoso.atlassian.net")]
    [InlineData("https://contoso.atlassian.net/wiki")]
    [InlineData("https://Contoso.Atlassian.net/wiki/")]
    [InlineData("contoso.atlassian.net")]
    public void Site_spellings_normalize_to_one_principal(string site) =>
        Assert.Equal("confluence-space:https://contoso.atlassian.net/ENG", ConfluenceAccessPrincipals.Space(site, "ENG"));
}
