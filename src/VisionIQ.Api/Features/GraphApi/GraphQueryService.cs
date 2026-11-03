using System.Text.Json;
using VisionIQ.Api.Features.Evidence;
using VisionIQ.Api.Features.Graph;

namespace VisionIQ.Api.Features.GraphApi;

public sealed record GraphNodeView(
    string Key, string Kind, string NativeId, string Provider, string Project, string? Repository,
    string SourceKind, string ExternalId, string SourceUrl, JsonElement Metadata);

public sealed record GraphEdgeView(string From, string To, string Relation, string Classification);

public sealed record GraphQueryResult(string Root, IReadOnlyList<GraphNodeView> Nodes, IReadOnlyList<GraphEdgeView> Edges);

public interface IGraphQueryService
{
    /// <summary>
    /// The authorized neighbourhood of an ADO work item, or null. Null means "not found OR not allowed": the two
    /// are deliberately indistinguishable. Only authorized nodes and edges are returned, with no hidden counts.
    /// </summary>
    Task<GraphQueryResult?> QueryWorkItemAsync(string project, string workItemId, EvidenceRequester requester, int hops, CancellationToken cancellationToken = default);
}

public sealed class GraphQueryService(AuthorizedGraphTraversal traversal, EvidenceQueryService evidence, GraphApiOptions options)
    : IGraphQueryService
{
    public async Task<GraphQueryResult?> QueryWorkItemAsync(
        string project, string workItemId, EvidenceRequester requester, int hops, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requester);
        if (hops is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(hops), "hops must be 1-3");
        }

        // Unknown projects and malformed IDs look exactly like "not found".
        if (string.IsNullOrWhiteSpace(project) || !options.ApprovedProjects.Contains(project, StringComparer.OrdinalIgnoreCase) ||
            workItemId is not { Length: >= 1 and <= 18 } || !workItemId.All(char.IsAsciiDigit))
        {
            return null;
        }

        var canonicalProject = options.ApprovedProjects.First(p => string.Equals(p, project, StringComparison.OrdinalIgnoreCase));
        var anchor = new EvidenceIdentity("azure_devops", options.AdoInstance, canonicalProject, null, "work_item_current", workItemId).ItemKey();
        var root = new GraphNode(anchor, GraphNodeKind.WorkItem, workItemId).NodeKey();
        var graph = await traversal.TraverseAsync(root, requester, hops, 100, cancellationToken);

        // Enrich only with authorized evidence; a node whose evidence is no longer readable is dropped with its edges.
        var nodes = new Dictionary<string, GraphNodeView>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            if (await evidence.FindLatestAuthorizedAsync(node.AnchorItemKey, requester, cancellationToken) is { } item)
            {
                using var metadata = JsonDocument.Parse(item.RedactedJson);
                nodes[node.NodeKey] = new GraphNodeView(node.NodeKey, GraphHashes.DatabaseValue(node.Kind), node.NativeId,
                    item.Identity.Provider, item.Identity.ProjectId, item.Identity.RepositoryId, item.Identity.Kind,
                    item.Identity.ExternalId, item.SourceUrl, metadata.RootElement.Clone());
            }
        }

        if (!nodes.ContainsKey(root))
        {
            return null;
        }

        var edges = graph.Edges
            .Where(edge => nodes.ContainsKey(edge.FromNodeKey) && nodes.ContainsKey(edge.ToNodeKey))
            .Select(edge => new GraphEdgeView(edge.FromNodeKey, edge.ToNodeKey,
                GraphHashes.DatabaseValue(edge.Relation), GraphHashes.DatabaseValue(edge.Classification)))
            .ToList();
        return new GraphQueryResult(root, nodes.Values.ToList(), edges);
    }
}
