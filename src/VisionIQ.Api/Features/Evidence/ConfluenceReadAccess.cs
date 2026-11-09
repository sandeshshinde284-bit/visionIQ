namespace VisionIQ.Api.Features.Evidence;

/// <summary>Access principal stored on Confluence page evidence (LRN-02).</summary>
public static class ConfluenceAccessPrincipals
{
    /// <summary>
    /// "confluence-space:{origin}/{space}": candidates only; the live page check decides. The site is reduced to its
    /// origin (scheme + host) so "https://x.atlassian.net" and "https://x.atlassian.net/wiki" match.
    /// </summary>
    public static string Space(string site, string space)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(space);
        return "confluence-space:" + Origin(site) + "/" + space;
    }

    /// <summary>Scheme and host (and non-default port) of a Confluence site address.</summary>
    public static string Origin(string site)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(site);
        var uri = new Uri(site.Contains("://", StringComparison.Ordinal) ? site : "https://" + site);
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }
}
