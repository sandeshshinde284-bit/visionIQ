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

    /// <summary>
    /// Repository of the target PR or commit when the source states it (B-11): ADO work-item
    /// artifact links are "vstfs:///Git/{type}/{projectId}%2F{repositoryId}%2F{id}".
    /// </summary>
    public string? ToRepositoryId { get; init; }

    /// <summary>
    /// m-10: when the source stated this link: the time of the source record it was read from
    /// (work item change, PR activity, commit date, build finish). Null when the source has none.
    /// </summary>
    public DateTimeOffset? SourceTimestamp { get; init; }
}

/// <summary>Extracts explicit links from source records (S04-11). Verification and storage belong to LRN-04.</summary>
public static partial class LinkExtractor
{
    private const string PullRequestArtifact = "vstfs:///Git/PullRequestId/";
    private const string CommitArtifact = "vstfs:///Git/Commit/";
    private const string BuildArtifact = "vstfs:///Build/Build/";
    private const string WorkItemApiSegment = "/_apis/wit/workItems/";

    /// <summary>ADO relation type on a work item pointing to the Test Case that tests it.</summary>
    public const string TestedByForward = "Microsoft.VSTS.Common.TestedBy-Forward";

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
                links.Add(new(EntityKind.WorkItem, from, EntityKind.PullRequest, pullRequest, "work_item_artifact", evidence)
                {
                    ToRepositoryId = ArtifactRepositoryId(relation.Url, PullRequestArtifact),
                });
            }
            else if (TryArtifactId(relation.Url, CommitArtifact, out var commit))
            {
                links.Add(new(EntityKind.WorkItem, from, EntityKind.Commit, commit.ToLowerInvariant(), "work_item_artifact", evidence)
                {
                    ToRepositoryId = ArtifactRepositoryId(relation.Url, CommitArtifact),
                });
            }
            else if (TryArtifactId(relation.Url, BuildArtifact, out var build))
            {
                links.Add(new(EntityKind.WorkItem, from, EntityKind.Build, build, "work_item_artifact", evidence));
            }
            else if (TryWorkItemId(relation.Url, out var other))
            {
                // LRN-07: ADO's "Tested By" relation declares that a Test Case is associated with this item.
                // It is a declared association only: not proof that the test passed or prevents recurrence.
                var type = string.Equals(relation.Rel, TestedByForward, StringComparison.OrdinalIgnoreCase)
                    ? "tested_by"
                    : "work_item_relation";
                links.Add(new(EntityKind.WorkItem, from, EntityKind.WorkItem, other, type, relation.Rel));
            }
        }

        return Stamp(Deduplicate(links), workItem.ChangedDate);
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

        return Stamp(Deduplicate(links), pullRequest.LastActivity);
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

        return Stamp(Deduplicate(links), commit.CommitterDate);
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

        return Stamp(Deduplicate(links), build.FinishTime ?? build.StartTime ?? build.QueueTime);
    }

    private static IReadOnlyList<SourceLink> Stamp(IReadOnlyList<SourceLink> links, DateTimeOffset? sourceTime) =>
        links.Select(link => link with { SourceTimestamp = sourceTime }).ToList();

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

    /// <summary>
    /// The repository segment of a Git artifact link ("{projectId}/{repositoryId}/{id}" after unescaping),
    /// or null when the link does not have exactly that shape. Never guessed from display text.
    /// </summary>
    private static string? ArtifactRepositoryId(string url, string prefix)
    {
        var segments = Uri.UnescapeDataString(url[prefix.Length..]).Split('/');
        return segments.Length == 3 && segments.All(segment => segment.Length > 0) ? segments[1] : null;
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
