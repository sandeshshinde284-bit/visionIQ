using System.Text.Json;
using VisionIQ.Ingestor.Ado;

namespace VisionIQ.Ingestor.Confluence;

/// <summary>
/// Confluence Cloud: REST v2 for spaces, pages, labels and versions; the v1 content-restriction API for view
/// restrictions (v2 has no equivalent). Unreadable or truncated restrictions are marked Unknown (fail closed).
/// </summary>
public sealed class ConfluenceCloudSource : IConfluenceSource
{
    private const string PageLimit = "250";
    private readonly ConfluenceHttp http;

    public ConfluenceCloudSource(ConfluenceHttp http)
    {
        ArgumentNullException.ThrowIfNull(http);
        this.http = http;
    }

    public string Tenant => http.Options.Tenant;

    public IReadOnlyList<string> SpaceKeys => http.Options.SpaceKeys;

    public async Task<IReadOnlyList<ConfluencePage>> GetPagesAsync(string spaceKey, CancellationToken cancellationToken = default)
    {
        var spaceId = await ResolveSpaceIdAsync(spaceKey, cancellationToken).ConfigureAwait(false);
        var pages = new List<ConfluencePage>();
        Uri? next = http.Api("api/v2/spaces/" + spaceId + "/pages", [new("body-format", "storage"), new("status", "current"), new("limit", PageLimit)]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (next is not null && seen.Add(next.AbsoluteUri))
        {
            using var document = await http.GetAsync(next, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var links = JsonRead.Child(root, "_links");
            var webBase = links is { } l ? JsonRead.String(l, "base") : null;
            pages.AddRange(JsonRead.Items(root, "results").Select(item => ParsePage(item, spaceKey, webBase)).OfType<ConfluencePage>());
            next = links is { } nextLinks && JsonRead.String(nextLinks, "next") is { } link ? http.Link(link) : null;
        }

        var withRestrictions = new List<ConfluencePage>(pages.Count);
        foreach (var page in pages)
        {
            withRestrictions.Add(page with { ReadRestriction = await GetReadRestrictionAsync(page.Id, cancellationToken).ConfigureAwait(false) });
        }

        return withRestrictions;
    }

    public async Task<ConfluencePage> EnrichAsync(ConfluencePage page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        List<string> labels;
        using (var document = await http.GetAsync(http.Api("api/v2/pages/" + page.Id + "/labels", [new("limit", PageLimit)]), cancellationToken).ConfigureAwait(false))
        {
            labels = JsonRead.Items(document.RootElement, "results").Select(item => JsonRead.String(item, "name")).OfType<string>().ToList();
        }

        List<ConfluenceVersion> versions;
        using (var document = await http.GetAsync(http.Api("api/v2/pages/" + page.Id + "/versions", [new("limit", "50")]), cancellationToken).ConfigureAwait(false))
        {
            versions = JsonRead.Items(document.RootElement, "results")
                .Select(item => JsonRead.Int(item, "number") is { } number
                    ? new ConfluenceVersion(number, JsonRead.Date(item, "createdAt"), JsonRead.String(item, "message"), JsonRead.Bool(item, "minorEdit"))
                    : null)
                .OfType<ConfluenceVersion>()
                .OrderBy(version => version.Number)
                .ToList();
        }

        return page with { Labels = labels, Versions = versions };
    }

    public async Task<int> CountPagesAsync(string spaceKey, CancellationToken cancellationToken = default)
    {
        var spaceId = await ResolveSpaceIdAsync(spaceKey, cancellationToken).ConfigureAwait(false);
        var count = 0;
        Uri? next = http.Api("api/v2/spaces/" + spaceId + "/pages", [new("status", "current"), new("limit", PageLimit)]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (next is not null && seen.Add(next.AbsoluteUri))
        {
            using var document = await http.GetAsync(next, cancellationToken).ConfigureAwait(false);
            count += JsonRead.Items(document.RootElement, "results").Count();
            next = JsonRead.Child(document.RootElement, "_links") is { } links && JsonRead.String(links, "next") is { } link ? http.Link(link) : null;
        }

        return count;
    }

    public static ConfluencePage? ParsePage(JsonElement element, string spaceKey, string? webBase)
    {
        if (JsonRead.String(element, "id") is not { } id
            || JsonRead.String(element, "title") is not { } title
            || JsonRead.Child(element, "version") is not { } version
            || JsonRead.Int(version, "number") is not { } number
            || JsonRead.Date(version, "createdAt") is not { } modified)
        {
            return null;
        }

        var body = JsonRead.Child(element, "body") is { } bodies && JsonRead.Child(bodies, "storage") is { } storage
            ? JsonRead.String(storage, "value")
            : null;
        var webui = JsonRead.Child(element, "_links") is { } links ? JsonRead.String(links, "webui") : null;
        var parentType = JsonRead.String(element, "parentType");

        return new ConfluencePage(id, spaceKey, title, number, modified)
        {
            ParentId = JsonRead.String(element, "parentId"),
            ParentIsPage = parentType is null || parentType == "page",
            StorageBody = body ?? string.Empty,
            Url = webui is not null && webBase is not null ? webBase.TrimEnd('/') + webui : null,
        };
    }

    /// <summary>Parses a v1 restriction response. Truncated lists are treated as unknown, never as complete.</summary>
    public static PageRestriction ParseRestriction(JsonElement root)
    {
        if (JsonRead.Child(root, "restrictions") is not { } restrictions)
        {
            return PageRestriction.Unreadable;
        }

        var users = JsonRead.Child(restrictions, "user");
        var groups = JsonRead.Child(restrictions, "group");
        if (users is null || groups is null || IsTruncated(users.Value) || IsTruncated(groups.Value))
        {
            return PageRestriction.Unreadable;
        }

        var userIds = JsonRead.Items(users.Value, "results")
            .Select(user => JsonRead.String(user, "accountId") ?? JsonRead.String(user, "username") ?? JsonRead.String(user, "userKey"))
            .ToList();
        if (userIds.Any(user => user is null))
        {
            return PageRestriction.Unreadable;
        }

        var groupNames = JsonRead.Items(groups.Value, "results").Select(group => JsonRead.String(group, "name")).ToList();
        if (groupNames.Any(group => group is null))
        {
            return PageRestriction.Unreadable;
        }

        return new PageRestriction(userIds!, groupNames!);
    }

    private static bool IsTruncated(JsonElement list) =>
        JsonRead.Int(list, "size") is { } size && JsonRead.Int(list, "limit") is { } limit && size >= limit && limit > 0;

    private async Task<string> ResolveSpaceIdAsync(string spaceKey, CancellationToken cancellationToken)
    {
        using var document = await http.GetAsync(http.Api("api/v2/spaces", [new("keys", spaceKey)]), cancellationToken).ConfigureAwait(false);
        var space = JsonRead.Items(document.RootElement, "results").FirstOrDefault(item => string.Equals(JsonRead.String(item, "key"), spaceKey, StringComparison.OrdinalIgnoreCase));
        return space.ValueKind == JsonValueKind.Object && JsonRead.String(space, "id") is { } id
            ? id
            : throw new ConfluenceSourceException(System.Net.HttpStatusCode.NotFound, $"Space {spaceKey} was not found, or the account cannot see it.");
    }

    private async Task<PageRestriction> GetReadRestrictionAsync(string pageId, CancellationToken cancellationToken)
    {
        try
        {
            var uri = http.Api(
                "rest/api/content/" + pageId + "/restriction/byOperation/read",
                [new("expand", "restrictions.user,restrictions.group"), new("limit", "200")]);
            using var document = await http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            return ParseRestriction(document.RootElement);
        }
        catch (ConfluenceSourceException)
        {
            // Restrictions that cannot be read are never treated as "no restrictions".
            return PageRestriction.Unreadable;
        }
    }
}
