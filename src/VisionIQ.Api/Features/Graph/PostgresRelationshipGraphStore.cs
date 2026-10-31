using Npgsql;
using VisionIQ.Api.Configuration;

namespace VisionIQ.Api.Features.Graph;

/// <summary>Read/write boundary for immutable graph edges and scoped adjacency.</summary>
public interface IRelationshipGraphStore
{
    /// <summary>Save a fully evidenced assertion idempotently, rejecting mismatched replay.</summary>
    Task SaveAsync(GraphAssertion assertion, CancellationToken cancellationToken = default);

    /// <summary>Resolve an internal node and its canonical evidence anchor.</summary>
    Task<GraphNode?> GetNodeAsync(string nodeKey, CancellationToken cancellationToken = default);

    /// <summary>Return the latest assertion for each outgoing relationship, including revocations.</summary>
    Task<IReadOnlyList<GraphNeighbor>> OutgoingAsync(
        string nodeKey, int limit, CancellationToken cancellationToken = default);
}

/// <summary>
/// PostgreSQL graph store. No raw source content, ACL grants, or public HTTP endpoint.
/// Constraints enforce that every node/provenance references LRN-03 evidence.
/// </summary>
public sealed class PostgresRelationshipGraphStore : IRelationshipGraphStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource source;

    /// <summary>Use restricted application-role PostgreSQL credentials.</summary>
    public PostgresRelationshipGraphStore(DatabaseOptions options)
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
    /// Persist both node scopes and a revision-specific assertion transactionally.
    /// A repeated assertion must have exactly the original immutable content hash.
    /// </summary>
    public async Task SaveAsync(GraphAssertion assertion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        assertion.Validate();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await SaveNodeAsync(connection, transaction, assertion.From, cancellationToken);
        await SaveNodeAsync(connection, transaction, assertion.To, cancellationToken);

        await using var insert = new NpgsqlCommand("""
            INSERT INTO evidence.graph_assertions (
                assertion_key, relationship_key, from_node_key, to_node_key,
                relation_type, classification, assertion_state,
                provenance_item_key, provenance_revision_id, source_asserted_at,
                content_sha256, ticket_fix_association_key
            ) VALUES (
                @assertion, @relationship, @from, @to,
                @relation, @class, @state, @provenance, @revision, @asserted, @hash, @fix
            )
            ON CONFLICT (assertion_key) DO NOTHING
            """, connection, transaction);
        insert.Parameters.AddWithValue("assertion", assertion.AssertionKey());
        insert.Parameters.AddWithValue("relationship", assertion.RelationshipKey());
        insert.Parameters.AddWithValue("from", assertion.From.NodeKey());
        insert.Parameters.AddWithValue("to", assertion.To.NodeKey());
        insert.Parameters.AddWithValue("relation", GraphHashes.DatabaseValue(assertion.Relation));
        insert.Parameters.AddWithValue("class", assertion.Classification.ToString().ToLowerInvariant());
        insert.Parameters.AddWithValue("state", assertion.State.ToString().ToLowerInvariant());
        insert.Parameters.AddWithValue("provenance", assertion.ProvenanceItemKey);
        insert.Parameters.AddWithValue("revision", assertion.ProvenanceRevision);
        insert.Parameters.AddWithValue("asserted", assertion.SourceAssertedAt.ToUniversalTime());
        insert.Parameters.AddWithValue("hash", assertion.ContentHash());
        insert.Parameters.AddWithValue("fix", (object?)assertion.TicketFixAssociationKey ?? DBNull.Value);

        if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await using var check = new NpgsqlCommand("""
                SELECT content_sha256 FROM evidence.graph_assertions
                WHERE assertion_key = @assertion
                """, connection, transaction);
            check.Parameters.AddWithValue("assertion", assertion.AssertionKey());
            if (!string.Equals((string?)await check.ExecuteScalarAsync(cancellationToken),
                assertion.ContentHash(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Immutable graph assertion revision conflict.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Insert a node while preventing silent collisions of its evidence anchor or type.</summary>
    private static async Task SaveNodeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        GraphNode node, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO evidence.graph_nodes (node_key, anchor_item_key, node_kind, native_id)
            VALUES (@key, @anchor, @kind, @native)
            ON CONFLICT (node_key) DO NOTHING
            """, connection, transaction);
        command.Parameters.AddWithValue("key", node.NodeKey());
        command.Parameters.AddWithValue("anchor", node.AnchorItemKey);
        command.Parameters.AddWithValue("kind", GraphHashes.DatabaseValue(node.Kind));
        command.Parameters.AddWithValue("native", node.NativeId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await using var check = new NpgsqlCommand("""
                SELECT (anchor_item_key = @anchor AND node_kind = @kind AND native_id = @native)
                FROM evidence.graph_nodes WHERE node_key = @key
                """, connection, transaction);
            check.Parameters.AddWithValue("key", node.NodeKey());
            check.Parameters.AddWithValue("anchor", node.AnchorItemKey);
            check.Parameters.AddWithValue("kind", GraphHashes.DatabaseValue(node.Kind));
            check.Parameters.AddWithValue("native", node.NativeId);
            if (await check.ExecuteScalarAsync(cancellationToken) is not true)
            {
                throw new InvalidOperationException("Graph node identity collision.");
            }
        }
    }

    /// <summary>Resolve an internal node without returning any external source content.</summary>
    public async Task<GraphNode?> GetNodeAsync(
        string nodeKey, CancellationToken cancellationToken = default)
    {
        GraphHashes.RequireKey(nodeKey, nameof(nodeKey));
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT anchor_item_key, node_kind, native_id
            FROM evidence.graph_nodes WHERE node_key = @node
            """, connection);
        command.Parameters.AddWithValue("node", nodeKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new GraphNode(reader.GetString(0), ParseNodeKind(reader.GetString(1)),
            reader.GetString(2));
    }

    /// <summary>
    /// Select the latest version of each outgoing assertion; latest retractions
    /// suppress older active assertions in the ACL-safe traversal layer.
    /// </summary>
    public async Task<IReadOnlyList<GraphNeighbor>> OutgoingAsync(
        string nodeKey, int limit, CancellationToken cancellationToken = default)
    {
        GraphHashes.RequireKey(nodeKey, nameof(nodeKey));
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        const string sql = """
            SELECT latest.to_node_key, n.anchor_item_key,
                   latest.provenance_item_key, latest.provenance_revision_id,
                   latest.relation_type, latest.classification, latest.assertion_state
            FROM (
                SELECT DISTINCT ON (g.relationship_key)
                    g.to_node_key, g.provenance_item_key, g.provenance_revision_id,
                    g.relation_type, g.classification, g.assertion_state,
                    g.relationship_key, g.ticket_fix_association_key
                FROM evidence.graph_assertions g
                WHERE g.from_node_key = @node
                ORDER BY g.relationship_key, g.source_asserted_at DESC,
                         g.observed_at DESC, g.assertion_key DESC
            ) AS latest
            JOIN evidence.graph_nodes n ON n.node_key = latest.to_node_key
            LEFT JOIN evidence.ticket_fix_links fix
              ON fix.association_key = latest.ticket_fix_association_key
            WHERE latest.assertion_state = 'active'
              AND latest.classification IN ('explicit','verified')
              AND (latest.ticket_fix_association_key IS NULL
                   OR (fix.association_key IS NOT NULL
                       AND fix.review_status IS DISTINCT FROM 'disputed'
                       AND jsonb_array_length(fix.revert_commit_shas) = 0))
            ORDER BY latest.relationship_key
            LIMIT @limit
            """;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("node", nodeKey);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var output = new List<GraphNeighbor>();
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new GraphNeighbor(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), ParseRelationKind(reader.GetString(4)),
                Enum.Parse<GraphClassification>(reader.GetString(5), ignoreCase: true)));
        }

        return output;
    }

    /// <summary>Parse only known, migrated graph entity types.</summary>
    private static GraphNodeKind ParseNodeKind(string value) =>
        Enum.Parse<GraphNodeKind>(value.Replace("_", "", StringComparison.Ordinal), true);

    /// <summary>Parse only known, migrated relationship types.</summary>
    private static GraphRelationKind ParseRelationKind(string value) =>
        Enum.Parse<GraphRelationKind>(value.Replace("_", "", StringComparison.Ordinal), true);

    /// <summary>Close the pooled PostgreSQL graph client.</summary>
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
