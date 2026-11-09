using VisionIQ.Api.Features.Evidence;

namespace VisionIQ.Api.Features.Graph;

/// <summary>
/// Trusted internal structural traversal of canonical, persisted graph edges.
/// LRN-07 hackathon scope defers end-user permissions; this method does NOT
/// authorize users or providers and MUST NOT be mapped to an anonymous HTTP API.
/// The graph API endpoint remains disabled until the deferred authentication
/// and source-permission checks are restored.
/// </summary>
public sealed class AuthorizedGraphTraversal(IRelationshipGraphStore graph)
{
    /// <summary>
    /// Traverse outgoing edges up to three hops and 100 nodes.
    /// Inferred, retracted or disputed relationships are excluded by the store.
    /// Caller permission checks are deferred: all returned structural nodes are internal-only.
    /// </summary>
    public async Task<AuthorizedGraph> TraverseAsync(
        string startNodeKey, EvidenceRequester requester,
        int maxHops = 3, int maxNodes = 100,
        CancellationToken cancellationToken = default)
    {
        GraphHashes.RequireKey(startNodeKey, nameof(startNodeKey));
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentNullException.ThrowIfNull(requester);
        if (maxHops is < 1 or > 3 || maxNodes is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maxHops),
                "Traversal is limited to 1-3 hops and 1-100 nodes.");
        }

        var root = await graph.GetNodeAsync(startNodeKey, cancellationToken);
        if (root is null)
        {
            return new AuthorizedGraph([], []);
        }

        var accepted = new Dictionary<string, AuthorizedGraphNode>(StringComparer.Ordinal)
        {
            [startNodeKey] = new(startNodeKey, root.AnchorItemKey, root.Kind, root.NativeId),
        };
        var edges = new List<AuthorizedGraphEdge>();
        var queue = new Queue<(string Key, int Hops)>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { startNodeKey };
        queue.Enqueue((startNodeKey, 0));

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
                        neighbor.DestinationNodeKey, target.AnchorItemKey, target.Kind, target.NativeId);
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
    }
}
