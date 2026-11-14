using Npgsql;
using VisionIQ.Api.Configuration;

namespace VisionIQ.Api.Features.Lessons;

/// <summary>LRN-10: the files one fix (a PR or a commit) changed, as read from the source.</summary>
public sealed record FixChangeSet(
    string SourceInstance, string ProjectId, string RepositoryId, string FixKind, string FixId,
    IReadOnlyList<string> Paths, bool Truncated, string ProvenanceItemKey, DateTimeOffset ObservedAt);

public interface IFixChangeSetStore
{
    Task UpsertAsync(FixChangeSet changeSet, CancellationToken cancellationToken = default);
}

/// <summary>Keeps the latest observed change set per fix (a PR's files change as it is updated).</summary>
public sealed class PostgresFixChangeSetStore : IFixChangeSetStore, IAsyncDisposable
{
    public const string UpsertSql = """
        INSERT INTO evidence.fix_change_sets (source_instance, project_id, repository_id, fix_kind, fix_id,
            paths, truncated, provenance_item_key, observed_at)
        VALUES (@instance, @project, @repository, @kind, @fix, @paths, @truncated, @provenance, @observed)
        ON CONFLICT (source_instance, project_id, repository_id, fix_kind, fix_id) DO UPDATE SET
            paths = EXCLUDED.paths, truncated = EXCLUDED.truncated,
            provenance_item_key = EXCLUDED.provenance_item_key, observed_at = EXCLUDED.observed_at
        WHERE evidence.fix_change_sets.observed_at <= EXCLUDED.observed_at
        """;

    private readonly NpgsqlDataSource source;

    public PostgresFixChangeSetStore(DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = options.Host,
            Port = options.Port,
            Database = options.Name,
            Username = options.User,
            Password = options.Password,
            Timeout = 10,
        };
        builder["SSL Mode"] = options.SslMode;
        source = NpgsqlDataSource.Create(builder.ConnectionString);
    }

    public async Task UpsertAsync(FixChangeSet changeSet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(UpsertSql, connection);
        command.Parameters.AddWithValue("instance", changeSet.SourceInstance);
        command.Parameters.AddWithValue("project", changeSet.ProjectId);
        command.Parameters.AddWithValue("repository", changeSet.RepositoryId);
        command.Parameters.AddWithValue("kind", changeSet.FixKind);
        command.Parameters.AddWithValue("fix", changeSet.FixId);
        command.Parameters.AddWithValue("paths", changeSet.Paths.ToArray());
        command.Parameters.AddWithValue("truncated", changeSet.Truncated);
        command.Parameters.AddWithValue("provenance", changeSet.ProvenanceItemKey);
        command.Parameters.AddWithValue("observed", changeSet.ObservedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => source.DisposeAsync();
}
