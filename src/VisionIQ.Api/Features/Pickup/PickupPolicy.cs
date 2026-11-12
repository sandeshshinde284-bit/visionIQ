using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VisionIQ.Api.Features.Pickup;

public enum TicketKind
{
    Defect,
    Enhancement,
}

/// <summary>
/// One change to a work item, as seen by pickup. Built from an ADO service-hook body or, by the reconciliation
/// sweep, from the work item's revision history; both go through the same <see cref="PickupPolicy.Evaluate"/>.
/// </summary>
/// <param name="Revision">The work-item revision this change produced (for a pickup: the revision that entered the pickup state).</param>
/// <param name="StateBefore">State before the change; null when the item was created by it.</param>
/// <param name="AssignedBefore">Whether the item had an assignee before the change.</param>
/// <param name="ChangedById">ADO identity id of whoever made the change; stored as the triggering actor.</param>
/// <param name="ChangedByName">Unique name of whoever made the change; compared with the bot list only, never stored.</param>
public sealed record TicketChange(
    string? OrganizationUrl,
    string Project,
    int WorkItemId,
    int Revision,
    string WorkItemType,
    string? StateBefore,
    string? StateAfter,
    bool Assigned,
    IReadOnlyList<string> Tags,
    string? ChangedById,
    string? ChangedByName,
    bool AssignedBefore = false);

/// <summary>Why a change does or does not start a run. Reasons are fixed codes, safe to log and store.</summary>
public sealed record PickupDecision(string? Reason, TicketKind Kind, ProjectPickupPolicy? Project)
{
    public bool Eligible => Reason is null;

    public static PickupDecision Ignore(string reason) => new(reason, default, null);
}

/// <summary>D05: how one ADO project picks up tickets, and where its runs look for code.</summary>
public sealed record ProjectPickupPolicy(
    string Project,
    string ProjectId,
    IReadOnlyList<string> DefectTypes,
    IReadOnlyList<string> EnhancementTypes,
    IReadOnlyList<string> PickupStates,
    bool RequireAssignee,
    IReadOnlyList<string> ExcludedTags,
    string RepositoryId,
    string Branch,
    string? BaseCommitSha,
    string SecurityScopeId)
{
    public TicketKind? KindOf(string workItemType) =>
        DefectTypes.Contains(workItemType, StringComparer.OrdinalIgnoreCase) ? TicketKind.Defect
        : EnhancementTypes.Contains(workItemType, StringComparer.OrdinalIgnoreCase) ? TicketKind.Enhancement
        : null;

    public bool IsPickupState(string? state) =>
        state is not null && PickupStates.Contains(state, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// "Actionable": in a pickup state and, when required, assigned. A pickup is the change that makes an item
    /// actionable, so an item assigned while already In Progress is picked up then, and later edits are not.
    /// </summary>
    public bool IsActionable(string? state, bool assigned) => IsPickupState(state) && (assigned || !RequireAssignee);
}

/// <summary>
/// S10-01 eligible ticket trigger rules (v1.11 §3.5, §8.1). A change starts a run only when the item belongs to
/// a configured project of the configured organization, is a configured defect or enhancement type, becomes
/// <em>actionable</em> with this change (enters a pickup state, or gets assigned while in one when the project
/// requires an assignee; later edits and comments do not re-trigger), was not made by a VisionIQ bot identity,
/// and carries no excluded tag.
/// </summary>
public sealed partial record PickupPolicy(
    string Revision,
    Uri? OrganizationUrl,
    IReadOnlyList<string> BotIdentities,
    IReadOnlyList<ProjectPickupPolicy> Projects)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// PICKUP_ALLOWED_WORK_ITEM_IDS: when set, only these work items can be picked up, by the webhook and by the
    /// sweep alike. Null means no restriction. It lets a pilot run on a few chosen items of a live organization
    /// without touching anyone else's tickets.
    /// </summary>
    public IReadOnlySet<int>? AllowedWorkItemIds { get; init; }

    public ProjectPickupPolicy? For(string project) =>
        Projects.FirstOrDefault(p => string.Equals(p.Project, project, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the work item may be picked up: no allowlist is set, or it is on the list.</summary>
    public bool IsWorkItemAllowed(int workItemId) => AllowedWorkItemIds is null || AllowedWorkItemIds.Contains(workItemId);

    /// <summary>True when a delivery's organization is the configured one (or none is configured).</summary>
    public bool IsOrganizationAllowed(string? organizationUrl) =>
        OrganizationUrl is null ||
        string.Equals(Normalize(organizationUrl), Normalize(OrganizationUrl.AbsoluteUri), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the ADO identity (id or unique name) is one of VisionIQ's own bot identities.</summary>
    public bool IsBot(string? identityId, string? uniqueName) =>
        BotIdentities.Any(bot =>
            string.Equals(bot, identityId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(bot, uniqueName, StringComparison.OrdinalIgnoreCase));

    public PickupDecision Evaluate(TicketChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!IsOrganizationAllowed(change.OrganizationUrl))
        {
            return PickupDecision.Ignore("organization_not_allowed");
        }

        if (!IsWorkItemAllowed(change.WorkItemId))
        {
            return PickupDecision.Ignore("work_item_not_allowlisted");
        }

        var project = For(change.Project);
        if (project is null)
        {
            return PickupDecision.Ignore("project_not_configured");
        }

        if (project.KindOf(change.WorkItemType) is not { } kind)
        {
            return PickupDecision.Ignore("type_not_eligible");
        }

        if (!project.IsActionable(change.StateAfter, change.Assigned))
        {
            return PickupDecision.Ignore(project.IsPickupState(change.StateAfter) ? "unassigned" : "no_pickup_transition");
        }

        if (project.IsActionable(change.StateBefore, change.AssignedBefore))
        {
            return PickupDecision.Ignore("no_pickup_transition");
        }

        if (IsBot(change.ChangedById, change.ChangedByName))
        {
            return PickupDecision.Ignore("bot_update");
        }

        if (change.Tags.Any(tag => project.ExcludedTags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
        {
            return PickupDecision.Ignore("excluded_tag");
        }

        return new PickupDecision(null, kind, project);
    }

    /// <summary>Reads and validates the pickup policy file (see config/pickup-policy.example.json).</summary>
    /// <exception cref="InvalidOperationException">The policy is missing required values or is malformed.</exception>
    public static PickupPolicy Parse(string json)
    {
        PolicyFile? file;
        try
        {
            file = JsonSerializer.Deserialize<PolicyFile>(json, Json);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"Pickup policy is not valid JSON ({error.Path ?? "<root>"}).", error);
        }

        if (file is null)
        {
            throw new InvalidOperationException("Pickup policy is empty.");
        }

        var problems = new List<string>();
        if (!RevisionPattern().IsMatch(file.PolicyRevision ?? string.Empty))
        {
            problems.Add("policy_revision is required (letters, digits, _ . : -; at most 64)");
        }

        Uri? organization = null;
        if (file.OrganizationUrl is not null &&
            (!Uri.TryCreate(file.OrganizationUrl, UriKind.Absolute, out organization) || organization.Scheme != Uri.UriSchemeHttps))
        {
            problems.Add("organization_url must be an https URL");
        }

        var projects = new List<ProjectPickupPolicy>();
        foreach (var (entry, index) in (file.Projects ?? []).Select((p, i) => (p, i)))
        {
            var where = $"projects[{index}]";
            var projectId = entry.ProjectId ?? entry.Project;
            var defects = Clean(entry.DefectTypes);
            var enhancements = Clean(entry.EnhancementTypes);
            var states = Clean(entry.PickupStates);
            if (string.IsNullOrWhiteSpace(entry.Project))
            {
                problems.Add($"{where}.project is required");
            }

            if (!IdentifierPattern().IsMatch(projectId ?? string.Empty))
            {
                problems.Add($"{where}.project_id is required when the project name has characters other than letters, digits and _ . : / -");
            }

            if (defects.Count + enhancements.Count == 0)
            {
                problems.Add($"{where} needs at least one defect_types or enhancement_types entry");
            }

            if (defects.Intersect(enhancements, StringComparer.OrdinalIgnoreCase).Any())
            {
                problems.Add($"{where}: a work-item type cannot be both a defect and an enhancement");
            }

            if (states.Count == 0)
            {
                problems.Add($"{where}.pickup_states is required (decision D05)");
            }

            if (!IdentifierPattern().IsMatch(entry.RepositoryId ?? string.Empty))
            {
                problems.Add($"{where}.repository_id is required");
            }

            if (!IdentifierPattern().IsMatch(entry.SecurityScopeId ?? string.Empty))
            {
                problems.Add($"{where}.security_scope_id is required");
            }

            if (entry.BaseCommitSha is not null && !CommitPattern().IsMatch(entry.BaseCommitSha))
            {
                problems.Add($"{where}.base_commit_sha must be a full lowercase commit SHA");
            }

            projects.Add(new ProjectPickupPolicy(
                entry.Project?.Trim() ?? string.Empty,
                projectId?.Trim() ?? string.Empty,
                defects,
                enhancements,
                states,
                entry.RequireAssignee ?? true,
                Clean(entry.ExcludedTags),
                entry.RepositoryId ?? string.Empty,
                string.IsNullOrWhiteSpace(entry.Branch) ? "main" : entry.Branch.Trim(),
                entry.BaseCommitSha,
                entry.SecurityScopeId ?? string.Empty));
        }

        if (projects.Count == 0)
        {
            problems.Add("projects needs at least one entry");
        }

        if (projects.GroupBy(p => p.Project, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            problems.Add("each project may appear only once");
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Invalid pickup policy: " + string.Join("; ", problems) + ".");
        }

        return new PickupPolicy(file.PolicyRevision!, organization, Clean(file.BotIdentities), projects);
    }

    private static string Normalize(string? url) => (url ?? string.Empty).Trim().TrimEnd('/');

    private static List<string> Clean(IEnumerable<string?>? values) =>
        (values ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    [GeneratedRegex("^[A-Za-z0-9_.:-]{1,64}$")]
    private static partial Regex RevisionPattern();

    [GeneratedRegex("^[A-Za-z0-9_.:/-]{1,256}$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^[0-9a-f]{40}([0-9a-f]{24})?$")]
    private static partial Regex CommitPattern();

    private sealed record PolicyFile(
        string? PolicyRevision,
        string? OrganizationUrl,
        List<string?>? BotIdentities,
        List<ProjectFile>? Projects);

    private sealed record ProjectFile(
        string? Project,
        string? ProjectId,
        List<string?>? DefectTypes,
        List<string?>? EnhancementTypes,
        List<string?>? PickupStates,
        bool? RequireAssignee,
        List<string?>? ExcludedTags,
        string? RepositoryId,
        string? Branch,
        string? BaseCommitSha,
        string? SecurityScopeId);
}
