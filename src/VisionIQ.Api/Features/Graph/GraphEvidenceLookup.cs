using Npgsql;
using NpgsqlTypes;

namespace VisionIQ.Api.Features.Graph;

/// <summary>Shared exact-identity lookups for graph materializers. Nothing is resolved by name similarity.</summary>
internal sealed class GraphEvidenceLookup(NpgsqlDataSource source)
{
    public const string FindSql = """
        SELECT item_key FROM evidence.source_items
        WHERE provider = 'azure_devops' AND source_instance = @source AND project_id = @project
          AND repository_id IS NOT DISTINCT FROM @repo AND kind = @kind AND external_id = @id
          AND latest_revision_id IS NOT NULL
        """;

    /// <summary>The canonical item for an exact provider identity, or null when it is not stored.</summary>
    public async Task<string?> FindAsync(
        string instance, string project, string? repository, string kind, string id, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(FindSql, connection);
        command.Parameters.AddWithValue("source", instance);
        command.Parameters.AddWithValue("project", project);
        command.Parameters.Add(new NpgsqlParameter("repo", NpgsqlDbType.Text) { Value = (object?)repository ?? DBNull.Value });
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("id", id);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>When the cited revision was stated by its source; null when that revision is not stored.</summary>
    public async Task<DateTimeOffset?> RevisionTimeAsync(string itemKey, string revision, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT source_updated_at FROM evidence.source_revisions
            WHERE item_key = @key AND provider_revision_id = @revision
            """, connection);
        command.Parameters.AddWithValue("key", itemKey);
        command.Parameters.AddWithValue("revision", revision);
        return await command.ExecuteScalarAsync(cancellationToken) switch
        {
            DateTimeOffset time => time,
            DateTime timestamp => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
            _ => null,
        };
    }

    /// <summary>Parses "evidence:&lt;64-hex item key&gt;:&lt;revision&gt;".</summary>
    public static bool TryParseReference(string value, out string itemKey, out string revision)
    {
        itemKey = revision = string.Empty;
        if (value is null || !value.StartsWith("evidence:", StringComparison.Ordinal) || value.Length < 9 + 64 + 2 || value[73] != ':')
        {
            return false;
        }

        itemKey = value.Substring(9, 64);
        revision = value[74..];
        return itemKey.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && !string.IsNullOrWhiteSpace(revision) && revision.Length <= 512;
    }
}
