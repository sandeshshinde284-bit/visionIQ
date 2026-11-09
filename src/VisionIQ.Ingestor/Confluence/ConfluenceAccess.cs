namespace VisionIQ.Ingestor.Confluence;

/// <summary>Walks a page's ancestor chain (S04-09's page-hierarchy metadata, not an access decision).</summary>
public static class ConfluenceAccess
{
    private const int MaxDepth = 100;

    /// <summary>Ancestor IDs from the immediate parent upward, as far as they are known.</summary>
    public static IReadOnlyList<string> AncestorIds(ConfluencePage page, IReadOnlyDictionary<string, ConfluencePage> byId)
    {
        ArgumentNullException.ThrowIfNull(page);
        return Chain(page, byId, out _).Skip(1).Select(ancestor => ancestor.Id).ToList();
    }

    private static List<ConfluencePage> Chain(ConfluencePage page, IReadOnlyDictionary<string, ConfluencePage> byId, out string? brokenReason)
    {
        brokenReason = null;
        var chain = new List<ConfluencePage> { page };

        // Data Center returns the full ancestor list (root-down) with the page itself, so we can
        // walk it directly. Cloud's v2 API gives only the immediate ParentId, so that case falls
        // through to the one-hop-at-a-time walk below instead.
        if (page.AncestorIds.Count > 0)
        {
            // M-2: the same loop/depth protection as the parent walk below. A malformed or
            // repeated ancestor list makes the ACL incomplete (hidden), never silently trusted.
            var seen = new HashSet<string>(StringComparer.Ordinal) { page.Id };
            foreach (var ancestorId in page.AncestorIds.Reverse())
            {
                if (!seen.Add(ancestorId) || chain.Count > MaxDepth)
                {
                    brokenReason = "The page ancestor list contains a loop or is too deep.";
                    break;
                }

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
