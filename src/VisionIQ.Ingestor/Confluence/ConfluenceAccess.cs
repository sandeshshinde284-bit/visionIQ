namespace VisionIQ.Ingestor.Confluence;

/// <summary>
/// The person asking. <see cref="Spaces"/> are the approved spaces this person can read in Confluence.
/// Without a known identity only unrestricted pages are visible.
/// </summary>
public sealed record ConfluenceViewer(string? UserId, IReadOnlySet<string> Groups, IReadOnlySet<string> Spaces)
{
    public static ConfluenceViewer Create(string? userId, IEnumerable<string> groups, IEnumerable<string> spaces) =>
        new(userId, new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase), new HashSet<string>(spaces, StringComparer.OrdinalIgnoreCase));
}

/// <summary>Computes effective page permissions and decides visibility (S04-09). Every doubt hides the page.</summary>
public static class ConfluenceAccess
{
    private const int MaxDepth = 100;

    /// <summary>Effective permissions for every page: its own view restriction plus every restricted ancestor.</summary>
    public static IReadOnlyDictionary<string, ConfluencePageAcl> ComputeAcls(IReadOnlyList<ConfluencePage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var byId = pages.ToDictionary(page => page.Id, StringComparer.Ordinal);
        var acls = new Dictionary<string, ConfluencePageAcl>(StringComparer.Ordinal);

        foreach (var page in pages)
        {
            var levels = new List<RestrictionLevel>();
            var chain = Chain(page, byId, out var brokenReason);
            var incomplete = brokenReason;
            foreach (var level in chain)
            {
                if (level.ReadRestriction.Unknown)
                {
                    incomplete ??= $"The view restrictions of page {level.Id} could not be read completely.";
                }
                else if (!level.ReadRestriction.IsEmpty)
                {
                    levels.Add(new RestrictionLevel(level.Id, level.ReadRestriction.Users, level.ReadRestriction.Groups));
                }
            }

            acls[page.Id] = new ConfluencePageAcl(page.Id, page.SpaceKey, incomplete is null, levels) { IncompleteReason = incomplete };
        }

        return acls;
    }

    /// <summary>Ancestor IDs from the immediate parent upward, as far as they are known.</summary>
    public static IReadOnlyList<string> AncestorIds(ConfluencePage page, IReadOnlyDictionary<string, ConfluencePage> byId)
    {
        ArgumentNullException.ThrowIfNull(page);
        return Chain(page, byId, out _).Skip(1).Select(ancestor => ancestor.Id).ToList();
    }

    /// <summary>True only when the viewer can read the space and passes every restricted level of a complete ACL.</summary>
    public static bool CanView(ConfluencePageAcl acl, ConfluenceViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(acl);
        ArgumentNullException.ThrowIfNull(viewer);
        if (!acl.Complete || !viewer.Spaces.Contains(acl.SpaceKey))
        {
            return false;
        }

        foreach (var level in acl.Levels)
        {
            var byUser = viewer.UserId is { } userId && level.Users.Contains(userId, StringComparer.Ordinal);
            var byGroup = level.Groups.Any(viewer.Groups.Contains);
            if (!byUser && !byGroup)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Keeps only what the viewer may see. Use this before anything reaches a prompt or a brief.</summary>
    public static IEnumerable<T> Visible<T>(IEnumerable<T> items, Func<T, ConfluencePageAcl> aclOf, ConfluenceViewer viewer)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(aclOf);
        return items.Where(item => CanView(aclOf(item), viewer));
    }

    private static List<ConfluencePage> Chain(ConfluencePage page, IReadOnlyDictionary<string, ConfluencePage> byId, out string? brokenReason)
    {
        brokenReason = null;
        var chain = new List<ConfluencePage> { page };

        if (page.AncestorIds.Count > 0)
        {
            foreach (var ancestorId in page.AncestorIds.Reverse())
            {
                if (!byId.TryGetValue(ancestorId, out var ancestor))
                {
                    brokenReason = $"Ancestor page {ancestorId} is not visible to the ingestion account.";
                    break;
                }

                chain.Add(ancestor);
            }

            return chain;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal) { page.Id };
        var current = page;
        while (current.ParentId is { } parentId)
        {
            if (!current.ParentIsPage)
            {
                brokenReason = $"Page {current.Id} sits under a non-page parent whose restrictions are not read.";
                break;
            }

            if (!byId.TryGetValue(parentId, out var parent))
            {
                brokenReason = $"Parent page {parentId} is not visible to the ingestion account.";
                break;
            }

            if (!visited.Add(parent.Id) || chain.Count > MaxDepth)
            {
                brokenReason = "The page hierarchy contains a loop.";
                break;
            }

            chain.Add(parent);
            current = parent;
        }

        return chain;
    }
}
