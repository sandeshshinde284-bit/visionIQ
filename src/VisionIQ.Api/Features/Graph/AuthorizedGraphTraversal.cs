using System.Text.Json;
using Npgsql;
using VisionIQ.Api.Configuration;
using VisionIQ.Api.Features.Evidence;

namespace VisionIQ.Api.Features.Graph;

/// <summary>Checks the ACL stored with the EXACT revision cited by an edge.</summary>
public interface IGraphRevisionAccess
{
    /// <summary>Do not substitute a newer revision's permissions for historical proof.</summary>
    Task<bool> CanReadRevisionAsync(
        string itemKey, string revision, EvidenceRequester requester,
        CancellationToken cancellationToken = default);
}

/// <summary>Reads only ACL metadata for immutable archived evidence revisions.</summary>
public sealed class PostgresGraphRevisionAccess : IGraphRevisionAccess, IAsyncDisposable
{
    private readonly NpgsqlDataSource source;

    /// <summary>Connect using the least-privileged API identity.</summary>
    public PostgresGraphRevisionAccess(DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = options.Host, Port = options.Port, Database = options.Name,
            Username = options.User, Password = options.Password, Timeout = 10,
        };
        builder["SSL Mode"] = options.SslMode;
        source = NpgsqlDataSource.Create(builder.ConnectionString);
    }

    /// <summary>
    /// Denies unknown, unverified, or nonmatching historical ACLs.
    /// Current provider access is independently checked by EvidenceQueryService.
    /// </summary>
    public async Task<bool> CanReadRevisionAsync(
        string itemKey, string revision, EvidenceRequester requester,
        CancellationToken cancellationToken = default)
    {
        GraphHashes.RequireKey(itemKey, nameof(itemKey));
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        ArgumentNullException.ThrowIfNull(requester);
        var principals = requester.PrincipalIds();
        if (principals.Count == 0)
        {
            return false;
        }

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT acl_verified, acl_principals::text
            FROM evidence.source_revisions
            WHERE item_key = @item AND provider_revision_id = @revision
            """, connection);
        command.Parameters.AddWithValue("item", itemKey);
        command.Parameters.AddWithValue("revision", revision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(0))
        {
            return false;
        }

        using var acl = JsonDocument.Parse(reader.GetString(1));
        return acl.RootElement.ValueKind == JsonValueKind.Array &&
            acl.RootElement.EnumerateArray().Any(value =>
                value.ValueKind == JsonValueKind.String &&
                value.GetString() is { } id && principals.Contains(id));
    }

    /// <summary>Dispose of the read-only connection pool.</summary>
    public ValueTask DisposeAsync() => source.DisposeAsync();
}

/// <summary>
/// Trusted bounded graph traversal. Every visited node and every evidence
/// revision must independently pass source ACL + current provider checks.
/// No anonymous or unverified evidence can leak through relationship edges.
/// </summary>
public sealed class AuthorizedGraphTraversal(
    IRelationshipGraphStore graph,
    EvidenceQueryService evidence,
    IGraphRevisionAccess revisions)
{
    /// <summary>
    /// Traverse outgoing edges up to three hops and 100 nodes.
    /// Inferred, retracted or disputed relationships are excluded by the store.
    /// </summary>
    public async Task<AuthorizedGraph> TraverseAsync(
        string startNodeKey, EvidenceRequester requester,
        int maxHops = 3, int maxNodes = 100,
        CancellationToken cancellationToken = default)
    {
        GraphHashes.RequireKey(startNodeKey, nameof(startNodeKey));
        ArgumentNullException.ThrowIfNull(requester);
        if (maxHops is < 1 or > 3 || maxNodes is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maxHops),
                "Traversal is limited to 1-3 hops and 1-100 nodes.");
        }

        var root = await graph.GetNodeAsync(startNodeKey, cancellationToken);
        if (root is null || !await AuthorizedAsync(root.AnchorItemKey, requester, cancellationToken))
        {
            return new AuthorizedGraph([], []);
        }

        var accepted = new Dictionary<string, AuthorizedGraphNode>(StringComparer.Ordinal)
        {
            [startNodeKey] = new(startNodeKey, root.AnchorItemKey, root.Kind),
        };
        var edges = new List<AuthorizedGraphEdge>();
        var queue = new Queue<(string Key, int Hops)>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { startNodeKey };
        queue.Enqueue((startNodeKey, 0));
        var accessCache = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [root.AnchorItemKey] = true,
        };

        while (queue.Count > 0 && accepted.Count < maxNodes)
        {
            var (nodeKey, hops) = queue.Dequeue();
            if (hops >= maxHops)
            {
                continue;
            }

            var neighbors = await graph.OutgoingAsync(nodeKey, 200, cancellationToken);
            foreach (var neighbor in neighbors)
            {
                // Never reveal even an opaque unauthorized neighbor edge.
                if (!await CanAccessAsync(neighbor.DestinationAnchorKey) ||
                    !await CanAccessAsync(neighbor.ProvenanceItemKey) ||
                    !await revisions.CanReadRevisionAsync(
                        neighbor.ProvenanceItemKey, neighbor.ProvenanceRevision,
                        requester, cancellationToken))
                {
                    continue;
                }

                var target = await graph.GetNodeAsync(
                    neighbor.DestinationNodeKey, cancellationToken);
                if (target is null ||
                    !string.Equals(target.AnchorItemKey, neighbor.DestinationAnchorKey,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (!accepted.ContainsKey(neighbor.DestinationNodeKey))
                {
                    if (accepted.Count >= maxNodes)
                    {
                        break;
                    }

                    accepted[neighbor.DestinationNodeKey] = new(
                        neighbor.DestinationNodeKey, target.AnchorItemKey, target.Kind);
                }

                edges.Add(new AuthorizedGraphEdge(nodeKey, neighbor.DestinationNodeKey,
                    neighbor.Relation, neighbor.Classification));
                if (visited.Add(neighbor.DestinationNodeKey))
                {
                    queue.Enqueue((neighbor.DestinationNodeKey, hops + 1));
                }
            }
        }

        return new AuthorizedGraph(accepted.Values.ToArray(), edges);

        /// <summary>Short-circuit repeated access checks for a single bounded query.</summary>
        async Task<bool> CanAccessAsync(string itemKey)
        {
            if (!accessCache.TryGetValue(itemKey, out var allowed))
            {
                allowed = await AuthorizedAsync(itemKey, requester, cancellationToken);
                accessCache[itemKey] = allowed;
            }

            return allowed;
        }
    }

    /// <summary>Require the canonical reader's stored and fresh-provider authorization.</summary>
    private async Task<bool> AuthorizedAsync(
        string itemKey, EvidenceRequester requester, CancellationToken cancellationToken)
    {
        return await evidence.FindLatestAuthorizedAsync(
            itemKey, requester, cancellationToken) is not null;
    }
}
