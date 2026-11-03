using System.Globalization;
using System.Text.Json;
using VisionIQ.Ingestor.Ado;

namespace VisionIQ.Ingestor.Confluence;

/// <summary>
/// Confluence Data Center / Server: REST v1 content API with version, body, ancestors, labels and
/// read restrictions expanded in one call. Version history is not listed (each new version is still kept).
/// </summary>
public sealed class ConfluenceDataCenterSource : IConfluenceSource
{
    private const int PageSize = 50;
    private const string Expand = "version,body.storage,ancestors,metadata.labels,restrictions.read.restrictions.user,restrictions.read.restrictions.group";
    private readonly ConfluenceHttp http;

    public ConfluenceDataCenterSource(ConfluenceHttp http)
    {
        ArgumentNullException.ThrowIfNull(http);
        this.http = http;
    }

    public string Tenant => http.Options.Tenant;

    public IReadOnlyList<string> SpaceKeys => http.Options.SpaceKeys;

    public async Task<IReadOnlyList<ConfluencePage>> GetPagesAsync(string spaceKey, CancellationToken cancellationToken = default)
    {
        await EnsureSpaceAsync(spaceKey, cancellationToken).ConfigureAwait(false);
        var pages = new List<ConfluencePage>();
        for (var start = 0; ; start += PageSize)
        {
            var uri = http.Api("rest/api/content", Query(spaceKey, start, Expand));
            using var document = await http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var webBase = JsonRead.Child(root, "_links") is { } links ? JsonRead.String(links, "base") : null;
            var results = JsonRead.Items(root, "results").ToList();
            pages.AddRange(results.Select(item => ParsePage(item, spaceKey, webBase)).OfType<ConfluencePage>());
            if (results.Count < PageSize)
            {
                return pages;
            }
        }
    }

    /// <summary>Labels already come with the page; version history is not listed on Data Center.</summary>
    public Task<ConfluencePage> EnrichAsync(ConfluencePage page, CancellationToken cancellationToken = default) => Task.FromResult(page);

    public async Task<int> CountPagesAsync(string spaceKey, CancellationToken cancellationToken = default)
    {
        await EnsureSpaceAsync(spaceKey, cancellationToken).ConfigureAwait(false);
        var count = 0;
        for (var start = 0; ; start += PageSize)
        {
            using var document = await http.GetAsync(http.Api("rest/api/content", Query(spaceKey, start, null)), cancellationToken).ConfigureAwait(false);
            var size = JsonRead.Items(document.RootElement, "results").Count();
            count += size;
            if (size < PageSize)
            {
                return count;
            }
        }
    }

    public static ConfluencePage? ParsePage(JsonElement element, string spaceKey, string? webBase)
    {
        if (JsonRead.String(element, "id") is not { } id
            || JsonRead.String(element, "title") is not { } title
            || JsonRead.Child(element, "version") is not { } version
            || JsonRead.Int(version, "number") is not { } number
            || JsonRead.Date(version, "when") is not { } modified)
        {
            return null;
        }

        var body = JsonRead.Child(element, "body") is { } bodies && JsonRead.Child(bodies, "storage") is { } storage
            ? JsonRead.String(storage, "value")
            : null;
        var ancestors = JsonRead.Items(element, "ancestors").Select(ancestor => JsonRead.String(ancestor, "id")).OfType<string>().ToList();
        var labels = JsonRead.Child(element, "metadata") is { } metadata && JsonRead.Child(metadata, "labels") is { } labelList
            ? JsonRead.Items(labelList, "results").Select(label => JsonRead.String(label, "name")).OfType<string>().ToList()
            : [];
        var webui = JsonRead.Child(element, "_links") is { } links ? JsonRead.String(links, "webui") : null;

        return new ConfluencePage(id, spaceKey, title, number, modified)
        {
            ParentId = ancestors.Count > 0 ? ancestors[^1] : null,
            AncestorIds = ancestors,
            StorageBody = body ?? string.Empty,
            Labels = labels,
            Url = webui is not null && webBase is not null ? webBase.TrimEnd('/') + webui : null,
            ReadRestriction = ParseRestriction(element),
        };
    }

    /// <summary>Reads restrictions.read.restrictions; anything missing or truncated is Unknown (fail closed).</summary>
    public static PageRestriction ParseRestriction(JsonElement page)
    {
        if (JsonRead.Child(page, "restrictions") is not { } restrictions
            || JsonRead.Child(restrictions, "read") is not { } read
            || JsonRead.Child(read, "restrictions") is not { } byType
            || JsonRead.Child(byType, "user") is not { } users
            || JsonRead.Child(byType, "group") is not { } groups)
        {
            return PageRestriction.Unreadable;
        }

        if (IsTruncated(users) || IsTruncated(groups))
        {
            return PageRestriction.Unreadable;
        }

        var userIds = JsonRead.Items(users, "results").Select(user => JsonRead.String(user, "username") ?? JsonRead.String(user, "userKey")).ToList();
        var groupNames = JsonRead.Items(groups, "results").Select(group => JsonRead.String(group, "name")).ToList();
        return userIds.Any(user => user is null) || groupNames.Any(group => group is null)
            ? PageRestriction.Unreadable
            : new PageRestriction(userIds!, groupNames!);
    }

    private static bool IsTruncated(JsonElement list) =>
        JsonRead.Int(list, "size") is { } size && JsonRead.Int(list, "limit") is { } limit && size >= limit && limit > 0;

    private static List<KeyValuePair<string, string>> Query(string spaceKey, int start, string? expand)
    {
        var query = new List<KeyValuePair<string, string>>
        {
            new("spaceKey", spaceKey),
            new("type", "page"),
            new("status", "current"),
            new("limit", PageSize.ToString(CultureInfo.InvariantCulture)),
            new("start", start.ToString(CultureInfo.InvariantCulture)),
        };
        if (expand is not null)
        {
            query.Add(new("expand", expand));
        }

        return query;
    }

    private async Task EnsureSpaceAsync(string spaceKey, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await http.GetAsync(http.Api("rest/api/space/" + Uri.EscapeDataString(spaceKey)), cancellationToken).ConfigureAwait(false);
        }
        catch (ConfluenceSourceException error) when (error.IsNotFound)
        {
            throw new ConfluenceSourceException(error.StatusCode, $"Space {spaceKey} was not found, or the account cannot see it.", error);
        }
    }
}
