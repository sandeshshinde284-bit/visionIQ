using System.Collections.Concurrent;
using System.Globalization;

namespace VisionIQ.Ingestor.Ado;

/// <summary>Live check of whether a person currently belongs to an Azure DevOps project (B-6).</summary>
public interface IAdoProjectMembership
{
    /// <summary>
    /// True when <paramref name="principalId"/> (ADO identity id or unique name, e.g. an email address)
    /// is a member of any team in <paramref name="project"/>. Throws <see cref="AdoSourceException"/>
    /// when membership cannot be read; callers must fail closed.
    /// </summary>
    Task<bool> IsMemberAsync(string project, string principalId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads project membership from the project's teams with the read-only connector identity.
/// Project-level access is the hackathon access model: anyone in a project team may see that
/// project's evidence. Area-path and repository-level restrictions are not modelled yet.
/// Successful reads are cached briefly; failures are never cached.
/// </summary>
public sealed class AdoTeamProjectMembership(AdoClient client, TimeSpan? cacheFor = null, TimeProvider? timeProvider = null)
    : IAdoProjectMembership
{
    private const int PageSize = 100;
    private const int MaxPages = 200;
    private readonly TimeSpan ttl = cacheFor ?? TimeSpan.FromMinutes(5);
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, HashSet<string> Members)> cache =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool> IsMemberAsync(string project, string principalId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return false;
        }

        if (!cache.TryGetValue(project, out var entry) || entry.Expires <= time.GetUtcNow())
        {
            entry = (time.GetUtcNow() + ttl, await ReadMembersAsync(project, cancellationToken).ConfigureAwait(false));
            cache[project] = entry;
        }

        return entry.Members.Contains(principalId.Trim());
    }

    private async Task<HashSet<string>> ReadMembersAsync(string project, CancellationToken cancellationToken)
    {
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var teamsPath = "_apis/projects/" + Uri.EscapeDataString(project) + "/teams";
        foreach (var team in await ReadPagedAsync(teamsPath, cancellationToken).ConfigureAwait(false))
        {
            if (JsonRead.String(team, "id") is not { Length: > 0 } teamId)
            {
                continue;
            }

            var membersPath = teamsPath + "/" + Uri.EscapeDataString(teamId) + "/members";
            foreach (var member in await ReadPagedAsync(membersPath, cancellationToken).ConfigureAwait(false))
            {
                if (JsonRead.Child(member, "identity") is not { } identity)
                {
                    continue;
                }

                foreach (var name in new[] { JsonRead.String(identity, "id"), JsonRead.String(identity, "uniqueName") })
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        members.Add(name.Trim());
                    }
                }
            }
        }

        return members;
    }

    private async Task<List<System.Text.Json.JsonElement>> ReadPagedAsync(string path, CancellationToken cancellationToken)
    {
        var items = new List<System.Text.Json.JsonElement>();
        for (var page = 0; page < MaxPages; page++)
        {
            var uri = client.BuildUri(null, path,
            [
                new("$top", PageSize.ToString(CultureInfo.InvariantCulture)),
                new("$skip", (page * PageSize).ToString(CultureInfo.InvariantCulture)),
            ]);
            using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            var batch = JsonRead.Items(response.Root, "value").Select(item => item.Clone()).ToList();
            items.AddRange(batch);
            if (batch.Count < PageSize)
            {
                return items;
            }
        }

        throw new AdoSourceException(null, "Project membership has more pages than allowed; access is denied.");
    }
}
