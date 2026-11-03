using VisionIQ.Api.Features.Evidence;
using VisionIQ.Ingestor.Cli;
using VisionIQ.Ingestor.Confluence;

namespace VisionIQ.Ingestor.Tests;

/// <summary>B-7: an unrestricted page is visible only to people who can view its space.</summary>
public sealed class ConfluenceSpaceAccessTests
{
    private const string Instance = "https://contoso.atlassian.net/wiki";

    private sealed class GrantSpace(string space, string principal) : IConfluenceSpaceAccess
    {
        public Task<bool> CanViewSpaceAsync(string spaceKey, EvidenceRequester requester, CancellationToken cancellationToken = default) =>
            Task.FromResult(spaceKey == space && requester.PrincipalId == principal);
    }

    private static async Task<(IConfluenceSource Source, string Unrestricted, ConfluencePageAcl Restricted)> Fixture()
    {
        var source = DemoConfluence.Source(DemoConfluence.CloudServer(), DemoConfluence.CloudOptions());
        var acls = ConfluenceAccess.ComputeAcls(await source.GetPagesAsync("ENG"));
        var unrestricted = acls.First(pair => pair.Value.Complete && !pair.Value.IsRestricted).Key;
        var restricted = acls.Values.First(acl => acl.Complete && acl.IsRestricted);
        return (source, unrestricted, restricted);
    }

    private static EvidenceIdentity Page(string id) => new("confluence", Instance, "ENG", null, "design_page", id);

    private static EvidenceRequester Person(string id) => new(id, new HashSet<string>());

    [Fact]
    public async Task By_default_an_unrestricted_page_is_hidden_because_space_access_is_unverified()
    {
        var (source, page, _) = await Fixture();

        var allowed = await new ConfluenceCurrentAccessVerifier(source, Instance).CanReadNowAsync(Page(page), Person("anyone"));

        Assert.False(allowed);
    }

    [Fact]
    public async Task An_unrestricted_page_is_visible_only_with_verified_space_access()
    {
        var (source, page, _) = await Fixture();
        var verifier = new ConfluenceCurrentAccessVerifier(source, Instance, new GrantSpace("ENG", "member"));

        Assert.True(await verifier.CanReadNowAsync(Page(page), Person("member")));
        Assert.False(await verifier.CanReadNowAsync(Page(page), Person("outsider")));
    }

    [Fact]
    public async Task Space_access_does_not_bypass_a_page_restriction()
    {
        var (source, _, restricted) = await Fixture();
        var verifier = new ConfluenceCurrentAccessVerifier(source, Instance, new GrantSpace("ENG", "member"));

        Assert.False(await verifier.CanReadNowAsync(Page(restricted.PageId), Person("member")));
    }
}
