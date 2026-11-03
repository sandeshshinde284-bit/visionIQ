namespace VisionIQ.Ingestor.Confluence;

public enum ConfluenceDeployment
{
    Cloud,
    DataCenter,
}

/// <summary>Confluence connection settings. The token never appears in <see cref="ToString"/>.</summary>
public sealed class ConfluenceOptions
{
    public required Uri BaseUrl { get; init; }

    /// <summary>Only these approved spaces are read, never the whole wiki.</summary>
    public required IReadOnlyList<string> SpaceKeys { get; init; }

    public required ConfluenceDeployment Deployment { get; init; }

    /// <summary>Cloud signs in with email and API token; Data Center uses a personal access token.</summary>
    public string? UserEmail { get; init; }

    public required string ApiToken { get; init; }

    public int MaxRetries { get; init; } = 4;

    public string Tenant => BaseUrl.Host;

    /// <summary>Scheme, host and port, ending with a slash.</summary>
    public Uri SiteRoot => new(BaseUrl.GetLeftPart(UriPartial.Authority) + "/");

    /// <summary>REST root: {site}/wiki on Cloud; the configured address (with any context path) on Data Center.</summary>
    public string ApiRoot => Deployment == ConfluenceDeployment.Cloud
        ? SiteRoot.AbsoluteUri.TrimEnd('/') + "/wiki"
        : BaseUrl.AbsoluteUri.TrimEnd('/');

    public static ConfluenceDeployment Infer(Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return baseUrl.Host.EndsWith(".atlassian.net", StringComparison.OrdinalIgnoreCase)
            ? ConfluenceDeployment.Cloud
            : ConfluenceDeployment.DataCenter;
    }

    public override string ToString() => $"{BaseUrl} ({Deployment}; spaces: {string.Join(", ", SpaceKeys)})";
}

/// <summary>View restrictions set directly on one page. <see cref="Unknown"/> means they could not be read completely.</summary>
public sealed record PageRestriction(IReadOnlyList<string> Users, IReadOnlyList<string> Groups)
{
    public bool Unknown { get; init; }

    public bool IsEmpty => !Unknown && Users.Count == 0 && Groups.Count == 0;

    public static PageRestriction None { get; } = new([], []);

    public static PageRestriction Unreadable { get; } = new([], []) { Unknown = true };
}

/// <summary>One historical version of a page (metadata only).</summary>
public sealed record ConfluenceVersion(int Number, DateTimeOffset? CreatedAt, string? Message, bool MinorEdit);

/// <summary>A page as read from Confluence, before normalization.</summary>
public sealed record ConfluencePage(string Id, string SpaceKey, string Title, int Version, DateTimeOffset LastModified)
{
    public string? ParentId { get; init; }

    /// <summary>False when the parent is not a page (for example a Cloud folder), whose restrictions are not read.</summary>
    public bool ParentIsPage { get; init; } = true;

    /// <summary>Ancestor IDs from the root down, when the API provides them (Data Center).</summary>
    public IReadOnlyList<string> AncestorIds { get; init; } = [];

    public string StorageBody { get; init; } = string.Empty;

    public string? Url { get; init; }

    public IReadOnlyList<string> Labels { get; init; } = [];

    public PageRestriction ReadRestriction { get; init; } = PageRestriction.None;

    public IReadOnlyList<ConfluenceVersion> Versions { get; init; } = [];
}

/// <summary>A restricted level in a page's ancestry: the viewer must be one of these users or in one of these groups.</summary>
public sealed record RestrictionLevel(string PageId, IReadOnlyList<string> Users, IReadOnlyList<string> Groups);

/// <summary>
/// Who may see a page. A viewer must pass every level. When <see cref="Complete"/> is false the restrictions are
/// ambiguous and nobody sees the page through VisionIQ.
/// </summary>
public sealed record ConfluencePageAcl(string PageId, string SpaceKey, bool Complete, IReadOnlyList<RestrictionLevel> Levels)
{
    public string? IncompleteReason { get; init; }

    public bool IsRestricted => Levels.Count > 0;

    /// <summary>Changes whenever the effective permissions change.</summary>
    public string Fingerprint
    {
        get
        {
            var canonical = SpaceKey + "|" + Complete + "|" + string.Join(
                ";",
                Levels.OrderBy(level => level.PageId, StringComparer.Ordinal).Select(level =>
                    level.PageId + ":" + string.Join(",", level.Users.Order(StringComparer.Ordinal))
                    + ":" + string.Join(",", level.Groups.Order(StringComparer.OrdinalIgnoreCase))));
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }
    }
}

public sealed record PageSection(string Heading, int Level, string Text);

/// <summary>A normalized, version-specific page snapshot. Each version is its own record; older ones are never overwritten.</summary>
public sealed record ConfluencePageSnapshot(string Tenant, string SpaceKey, string Id, int Version, string Title, DateTimeOffset LastModified)
{
    public string? ParentId { get; init; }

    public IReadOnlyList<string> AncestorIds { get; init; } = [];

    public string? Url { get; init; }

    public IReadOnlyList<string> Labels { get; init; } = [];

    public string Text { get; init; } = string.Empty;

    public IReadOnlyList<PageSection> Sections { get; init; } = [];

    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<ConfluenceVersion> Versions { get; init; } = [];

    public required ConfluencePageAcl Acl { get; init; }
}

/// <summary>A design decision found on a page, with the reason it was recognized.</summary>
public sealed record DesignDecisionRecord(string PageId, int Version, string SpaceKey, string Title, string DetectedBy)
{
    public string? Status { get; init; }

    public string? Outcome { get; init; }

    public IReadOnlyList<string> Labels { get; init; } = [];

    public string? Url { get; init; }

    public DateTimeOffset LastModified { get; init; }

    public required ConfluencePageAcl Acl { get; init; }
}

/// <summary>Reads pages from one Confluence deployment.</summary>
public interface IConfluenceSource
{
    string Tenant { get; }

    IReadOnlyList<string> SpaceKeys { get; }

    /// <summary>Every current page in the space, with its own view restriction. Read on every run.</summary>
    Task<IReadOnlyList<ConfluencePage>> GetPagesAsync(string spaceKey, CancellationToken cancellationToken = default);

    /// <summary>Adds what is only needed for new versions (labels, version history).</summary>
    Task<ConfluencePage> EnrichAsync(ConfluencePage page, CancellationToken cancellationToken = default);

    Task<int> CountPagesAsync(string spaceKey, CancellationToken cancellationToken = default);
}
