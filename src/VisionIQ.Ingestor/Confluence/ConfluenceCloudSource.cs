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
    private readonly Microsoft.Extensions.Logging.ILogger log;

    public ConfluenceCloudSource(ConfluenceHttp http, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        this.http = http;
        log = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public string Tenant => http.Options.Tenant;

    public IReadOnlyList<string> SpaceKeys => http.Options.SpaceKeys;

    public Task<IReadOnlyList<ConfluencePage>> GetPagesAsync(string spaceKey, CancellationToken cancellationToken = default) =>
        GetPagesCoreAsync(spaceKey, includeBody: true, cancellationToken);

    /// <summary>Read page identity, title, version, ancestry and restrictions, without requesting page bodies.</summary>
    public Task<IReadOnlyList<ConfluencePage>> GetPageMetadataAsync(string spaceKey, CancellationToken cancellationToken = default) =>
        GetPagesCoreAsync(spaceKey, includeBody: false, cancellationToken);

    private async Task<IReadOnlyList<ConfluencePage>> GetPagesCoreAsync(string spaceKey, bool includeBody, CancellationToken cancellationToken)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var spaceId = await ResolveSpaceIdAsync(spaceKey, cancellationToken).ConfigureAwait(false);
        var pages = new List<ConfluencePage>();
        Uri? next = includeBody
            ? http.Api("api/v2/spaces/" + spaceId + "/pages", [new("body-format", "storage"), new("status", "current"), new("limit", PageLimit)])
            : http.Api("api/v2/spaces/" + spaceId + "/pages", [new("status", "current"), new("limit", PageLimit)]);
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

        Logging.IngestionLog.ConfluencePagesDiscovered(log, spaceKey, pages.Count, timer.Elapsed.TotalMilliseconds);

        // v2's space-pages listing has no restrictions field, so each page needs its own v1 call.
        // R2-02: this used to be one request at a time, which made a real space with hundreds of
        // pages look hung (no log between "started" and "finished" for 12+ minutes). Now bounded
        // (ConfluenceOptions.RestrictionFetchConcurrency, modest by default) so several pages overlap
        // without bursting Confluence's rate limit; ConfluenceHttp's own Retry-After-aware backoff
        // still handles any individual request that gets throttled.
        var withRestrictions = new ConfluencePage[pages.Count];
        var completed = 0;
        // A misconfigured (zero/negative) value would make ParallelOptions throw at run time;
        // fall back to the documented default instead of surfacing that as a confusing crash.
        var concurrency = http.Options.RestrictionFetchConcurrency > 0
            ? http.Options.RestrictionFetchConcurrency
            : ConfluenceOptions.DefaultRestrictionFetchConcurrency;
        await Parallel.ForEachAsync(Enumerable.Range(0, pages.Count), new ParallelOptions
        {
            MaxDegreeOfParallelism = concurrency,
            CancellationToken = cancellationToken,
        }, async (index, token) =>
        {
            var page = pages[index];
            withRestrictions[index] = page with { ReadRestriction = await GetReadRestrictionAsync(page.Id, token).ConfigureAwait(false) };
            var done = Interlocked.Increment(ref completed);
            if (done % 25 == 0 || done == pages.Count)
            {
                Logging.IngestionLog.ConfluenceRestrictionsProgress(log, spaceKey, done, pages.Count, timer.Elapsed.TotalMilliseconds);
            }
        }).ConfigureAwait(false);

        return withRestrictions;
    }

    /// <summary>Fetch label names only. Never request page bodies or version histories for discovery.</summary>
    public async Task<IReadOnlyList<string>> GetPageLabelsAsync(string pageId, CancellationToken cancellationToken = default)
    {
        var labels = await ReadAllAsync(http.Api("api/v2/pages/" + Uri.EscapeDataString(pageId) + "/labels", [new("limit", PageLimit)]), cancellationToken).ConfigureAwait(false);
        return labels.Select(item => JsonRead.String(item, "name")).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<ConfluencePage> EnrichAsync(ConfluencePage page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        // M-15: follow the cursor instead of reading only the first 250 labels / 50 versions.
        var labels = (await ReadAllAsync(http.Api("api/v2/pages/" + page.Id + "/labels", [new("limit", PageLimit)]), cancellationToken).ConfigureAwait(false))
            .Select(item => JsonRead.String(item, "name"))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var versions = (await ReadAllAsync(http.Api("api/v2/pages/" + page.Id + "/versions", [new("limit", PageLimit)]), cancellationToken).ConfigureAwait(false))
            .Select(item => JsonRead.Int(item, "number") is { } number
                ? new ConfluenceVersion(number, JsonRead.Date(item, "createdAt"), JsonRead.String(item, "message"), JsonRead.Bool(item, "minorEdit"))
                : null)
            .OfType<ConfluenceVersion>()
            .DistinctBy(version => version.Number)
            .OrderBy(version => version.Number)
            .ToList();

        return page with { Labels = labels, Versions = versions };
    }

    /// <summary>Reads every page of a v2 collection via _links.next (max 20 pages, repeat-URL guard).</summary>
    private async Task<List<JsonElement>> ReadAllAsync(Uri first, CancellationToken cancellationToken)
    {
        const int MaxPages = 20;
        var items = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Uri? next = first;
        for (var pageNumber = 0; next is not null && pageNumber < MaxPages && seen.Add(next.AbsoluteUri); pageNumber++)
        {
            using var document = await http.GetAsync(next, cancellationToken).ConfigureAwait(false);
            items.AddRange(JsonRead.Items(document.RootElement, "results").Select(item => item.Clone()));
            next = JsonRead.Child(document.RootElement, "_links") is { } links && JsonRead.String(links, "next") is { } link ? http.Link(link) : null;
        }

        return items;
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
