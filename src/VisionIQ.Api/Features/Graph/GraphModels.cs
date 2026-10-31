using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace VisionIQ.Api.Features.Graph;

/// <summary>Supported, reviewed engineering node types.</summary>
public enum GraphNodeKind
{
    WorkItem, PullRequest, Commit, Repository, File, Symbol, DesignPage, CiRun,
}

/// <summary>Supported relationship types; never inferred from timestamps or name similarity.</summary>
public enum GraphRelationKind
{
    TicketPr, PrCommit, CommitFile, FileSymbol, FileDependency,
    RepositoryDependency, TicketCommit, DesignRelated, CiCommit,
}

/// <summary>The proof strength of a relationship, distinct from review and revocation.</summary>
public enum GraphClassification { Explicit, Inferred, Verified }

/// <summary>A versioned assertion's status; disputed/retracted edges are not traversable.</summary>
public enum GraphAssertionState { Active, Disputed, Retracted }

/// <summary>
/// A typed node, anchored to an existing immutable canonical evidence item.
/// File and symbol nodes inherit the permissions of their originating code scan.
/// </summary>
public sealed record GraphNode(
    string AnchorItemKey, GraphNodeKind Kind, string NativeId)
{
    /// <summary>Computes a collision-resistant identity without assuming global native IDs.</summary>
    public string NodeKey()
    {
        GraphHashes.RequireKey(AnchorItemKey, nameof(AnchorItemKey));
        if (string.IsNullOrWhiteSpace(NativeId) || NativeId.Length > 2048)
        {
            throw new ArgumentException("A bounded native node identity is required.");
        }

        return GraphHashes.Hash(AnchorItemKey, Kind.ToString(), NativeId);
    }
}

/// <summary>
/// One immutable assertion backed by a specific source revision.
/// New revisions record retractions/disputes without rewriting earlier history.
/// </summary>
public sealed record GraphAssertion(
    GraphNode From, GraphNode To, GraphRelationKind Relation,
    string ProvenanceItemKey, string ProvenanceRevision,
    GraphClassification Classification, GraphAssertionState State,
    DateTimeOffset SourceAssertedAt)
{
    /// <summary>Stable identity of the edge, independent of evidence revision.</summary>
    public string RelationshipKey()
    {
        Validate();
        return GraphHashes.Hash(From.NodeKey(), To.NodeKey(), Relation.ToString(), ProvenanceItemKey);
    }

    /// <summary>Identity for this specific immutable provenance revision.</summary>
    public string AssertionKey() =>
        GraphHashes.Hash(RelationshipKey(), ProvenanceRevision);

    /// <summary>Hash of all immutable columns to detect conflicting replays.</summary>
    public string ContentHash() =>
        GraphHashes.Hash(AssertionKey(), Classification.ToString(),
            State.ToString(), SourceAssertedAt.ToUniversalTime().ToString("O"));

    /// <summary>Reject missing source proof, invalid scope or self-referential assertions.</summary>
    public void Validate()
    {
        GraphHashes.RequireKey(ProvenanceItemKey, nameof(ProvenanceItemKey));
        _ = From.NodeKey();
        _ = To.NodeKey();
        if (From.NodeKey() == To.NodeKey() ||
            string.IsNullOrWhiteSpace(ProvenanceRevision) || ProvenanceRevision.Length > 512 ||
            !Enum.IsDefined(Relation) || !Enum.IsDefined(Classification) ||
            !Enum.IsDefined(State) || SourceAssertedAt == default)
        {
            throw new ArgumentException("Graph assertion lacks valid immutable source provenance.");
        }
    }
}

/// <summary>A minimal graph row for traversal, with no source data or ACL payload.</summary>
public sealed record GraphNeighbor(
    string DestinationNodeKey, string DestinationAnchorKey,
    string ProvenanceItemKey, string ProvenanceRevision,
    GraphRelationKind Relation, GraphClassification Classification);

/// <summary>An authorized graph node; the source's redacted data stays in the evidence service.</summary>
public sealed record AuthorizedGraphNode(
    string NodeKey, string AnchorItemKey, GraphNodeKind Kind);

/// <summary>An authorized edge with opaque node keys and proof type only.</summary>
public sealed record AuthorizedGraphEdge(
    string FromNodeKey, string ToNodeKey, GraphRelationKind Relation,
    GraphClassification Classification);

/// <summary>Bounded, fail-closed traversal results for a trusted internal caller.</summary>
public sealed record AuthorizedGraph(
    IReadOnlyList<AuthorizedGraphNode> Nodes, IReadOnlyList<AuthorizedGraphEdge> Edges);

/// <summary>Canonical SHA-256 over length-prefixed UTF-8 components (as LRN-03).</summary>
public static class GraphHashes
{
    /// <summary>Hash a sequence of scoped values without delimiter collisions.</summary>
    public static string Hash(params string[] values)
    {
        using var stream = new MemoryStream();
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> prefix = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
            stream.Write(prefix);
            stream.Write(bytes);
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    /// <summary>Require a canonical lowercase SHA-256 key.</summary>
    public static void RequireKey(string value, string parameter)
    {
        if (value is not { Length: 64 } ||
            !value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ArgumentException("Expected a lowercase 64-character SHA-256 key.", parameter);
        }
    }

    /// <summary>Use source-defined snake_case symbols for persisted enum values.</summary>
    public static string DatabaseValue<T>(T value) where T : struct, Enum =>
        string.Concat(value.ToString().Select((c, index) =>
            char.IsUpper(c) && index > 0 ? "_" + char.ToLowerInvariant(c) : char.ToString(c).ToLowerInvariant()));
}
