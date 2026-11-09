using VisionIQ.Api.Features.Evidence;

namespace VisionIQ.Api.Tests;

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
