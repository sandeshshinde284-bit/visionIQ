using System.Globalization;
using System.Text.RegularExpressions;
using VisionIQ.Ingestor.Ado;

namespace VisionIQ.Ingestor.Links;

public enum EntityKind
{
    WorkItem,
    PullRequest,
    Commit,
    Build,
}

/// <summary>A link the source system states itself. Never inferred from time, similarity or file overlap.</summary>
public sealed record SourceLink(EntityKind FromKind, string FromId, EntityKind ToKind, string ToId, string LinkType, string Evidence)
{
    public string Key => $"{FromKind}:{FromId}->{ToKind}:{ToId}:{LinkType}";
}

/// <summary>Extracts explicit links from source records (S04-11). Verification and storage belong to LRN-04.</summary>
public static partial class LinkExtractor
{
    private const string PullRequestArtifact = "vstfs:///Git/PullRequestId/";
    private const string CommitArtifact = "vstfs:///Git/Commit/";
    private const string BuildArtifact = "vstfs:///Build/Build/";
    private const string WorkItemApiSegment = "/_apis/wit/workItems/";

    public static IReadOnlyList<SourceLink> FromWorkItem(WorkItemRecord workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        var from = Text(workItem.Id);
        var links = new List<SourceLink>();

        foreach (var relation in workItem.Relations)
        {
            var evidence = relation.Name ?? relation.Rel;
            if (TryArtifactId(relation.Url, PullRequestArtifact, out var pullRequest))
            {
                links.Add(new(EntityKind.WorkItem, from, EntityKind.PullRequest, pullRequest, "work_item_artifact", evidence));
            }
            else if (TryArtifactId(relation.Url, CommitArtifact, out var commit))
            {
                links.Add(new(EntityKind.WorkItem, from, EntityKind.Commit, commit.ToLowerInvariant(), "work_item_artifact", evidence));
            }
            else if (TryArtifactId(relation.Url, BuildArtifact, out var build))
            {
                links.Add(new(EntityKind.WorkItem, from, EntityKind.Build, build, "work_item_artifact", evidence));
            }
            else if (TryWorkItemId(relation.Url, out var other))
            {
                links.Add(new(EntityKind.WorkItem, from, EntityKind.WorkItem, other, "work_item_relation", relation.Rel));
            }
        }

        return Deduplicate(links);
    }

    public static IReadOnlyList<SourceLink> FromPullRequest(PullRequestRecord pullRequest)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        var from = Text(pullRequest.Id);
        var links = new List<SourceLink>();
        links.AddRange(pullRequest.WorkItemIds.Select(id =>
            new SourceLink(EntityKind.PullRequest, from, EntityKind.WorkItem, Text(id), "pr_work_item", "pull request work items")));
        links.AddRange(pullRequest.CommitIds.Select(commit =>
            new SourceLink(EntityKind.PullRequest, from, EntityKind.Commit, commit.ToLowerInvariant(), "pr_commit", "pull request commits")));
        if (pullRequest.MergeCommitId is { } merge)
        {
            links.Add(new(EntityKind.PullRequest, from, EntityKind.Commit, merge.ToLowerInvariant(), "pr_merge_commit", "completed merge commit"));
        }

        return Deduplicate(links);
    }

    public static IReadOnlyList<SourceLink> FromCommit(CommitRecord commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var links = new List<SourceLink>();
        var message = commit.Comment;
        var from = commit.CommitId.ToLowerInvariant();

        foreach (Match match in MergedPullRequest().Matches(message))
        {
            links.Add(new(EntityKind.Commit, from, EntityKind.PullRequest, match.Groups[1].Value, "merge_message", match.Value));
        }

        foreach (Match match in RevertedCommit().Matches(message))
        {
            links.Add(new(EntityKind.Commit, from, EntityKind.Commit, match.Groups[1].Value.ToLowerInvariant(), "reverts", match.Value));
        }

        foreach (Match match in AzureBoardsMention().Matches(message))
        {
            links.Add(new(EntityKind.Commit, from, EntityKind.WorkItem, match.Groups[1].Value, "commit_mention", match.Value));
        }

        foreach (Match match in WorkItemMention().Matches(message))
        {
            links.Add(new(EntityKind.Commit, from, EntityKind.WorkItem, match.Groups[1].Value, "commit_mention", match.Value));
        }

        return Deduplicate(links);
    }

    public static IReadOnlyList<SourceLink> FromBuild(BuildRecord build)
    {
        ArgumentNullException.ThrowIfNull(build);
        var from = Text(build.Id);
        var links = new List<SourceLink>();
        if (build.SourceVersion is { } version && FullSha().IsMatch(version))
        {
            links.Add(new(EntityKind.Build, from, EntityKind.Commit, version.ToLowerInvariant(), "build_source_version", "build sourceVersion"));
        }

        if (build.PullRequestNumber is { } pullRequest)
        {
            links.Add(new(EntityKind.Build, from, EntityKind.PullRequest, Text(pullRequest), "build_pull_request", "build triggerInfo pr.number"));
        }

        return Deduplicate(links);
    }

    /// <summary>Keeps the first occurrence of each from-to-type link.</summary>
    public static IReadOnlyList<SourceLink> Deduplicate(IEnumerable<SourceLink> links) =>
        links.DistinctBy(link => link.Key, StringComparer.Ordinal).ToList();

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool TryArtifactId(string url, string prefix, out string id)
    {
        id = string.Empty;
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var unescaped = Uri.UnescapeDataString(url[prefix.Length..]);
        id = unescaped[(unescaped.LastIndexOf('/') + 1)..];
        return id.Length > 0;
    }

    private static bool TryWorkItemId(string url, out string id)
    {
        id = string.Empty;
        var index = url.IndexOf(WorkItemApiSegment, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        var candidate = url[(index + WorkItemApiSegment.Length)..];
        if (!int.TryParse(candidate, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        id = candidate;
        return true;
    }

    [GeneratedRegex(@"^Merged PR (\d+):", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex MergedPullRequest();

    [GeneratedRegex(@"This reverts commit ([0-9a-fA-F]{7,40})", RegexOptions.CultureInvariant)]
    private static partial Regex RevertedCommit();

    [GeneratedRegex(@"\bAB#(\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex AzureBoardsMention();

    // "#1234" in a message, but not inside a word or URL ("page#55"), an HTML entity ("&#123;"),
    // a pull request reference ("PR #12"), or a single digit ("#1").
    [GeneratedRegex(@"(?<![\w&/#]|PR\s)#(\d{2,})\b", RegexOptions.CultureInvariant)]
    private static partial Regex WorkItemMention();

    [GeneratedRegex(@"^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex FullSha();
}
