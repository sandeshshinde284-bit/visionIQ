using Npgsql;
using NpgsqlTypes;
using VisionIQ.Api.Configuration;

namespace VisionIQ.Api.Features.BuildLinks;

/// <summary>What one build built (commit and/or triggering pull request), as stated by one evidence revision.</summary>
public sealed record BuildLink(
    string SourceInstance,
    string ProjectId,
    string BuildId,
    string RepositoryId,
    string? CommitSha,
    string? PullRequestId,
    string EvidenceRef,
    DateTimeOffset SourceAssertedAt);

public interface IBuildLinkStore
{
    /// <summary>Stores the link; an older evidence revision never overwrites a newer one.</summary>
    Task UpsertAsync(BuildLink link, CancellationToken cancellationToken = default);
}

public sealed class PostgresBuildLinkStore : IBuildLinkStore, IAsyncDisposable
{
    // The WHERE guard compares the source's own asserted-at time, never observed_at/wall-clock, so a
    // replayed or out-of-order sync of an older build revision can touch observed_at but can never
    // clobber a link a newer revision already recorded.
    public const string UpsertSql = """
        INSERT INTO evidence.build_links (source_instance, project_id, build_id, repository_id,
            commit_sha, pull_request_id, evidence_ref, source_asserted_at)
        VALUES (@source, @project, @build, @repo, @sha, @pr, @ref, @at)
        ON CONFLICT (source_instance, project_id, build_id) DO UPDATE SET
            repository_id = EXCLUDED.repository_id,
            commit_sha = EXCLUDED.commit_sha,
            pull_request_id = EXCLUDED.pull_request_id,
            evidence_ref = EXCLUDED.evidence_ref,
            source_asserted_at = EXCLUDED.source_asserted_at,
            observed_at = now()
        WHERE evidence.build_links.source_asserted_at <= EXCLUDED.source_asserted_at
        """;

    private readonly NpgsqlDataSource source;

    public PostgresBuildLinkStore(DatabaseOptions options)
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

    public async Task UpsertAsync(BuildLink link, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(UpsertSql, connection);
        command.Parameters.AddWithValue("source", link.SourceInstance);
        command.Parameters.AddWithValue("project", link.ProjectId);
        command.Parameters.AddWithValue("build", link.BuildId);
        command.Parameters.AddWithValue("repo", link.RepositoryId);
        command.Parameters.Add(new NpgsqlParameter("sha", NpgsqlDbType.Text) { Value = (object?)link.CommitSha ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("pr", NpgsqlDbType.Text) { Value = (object?)link.PullRequestId ?? DBNull.Value });
        command.Parameters.AddWithValue("ref", link.EvidenceRef);
        command.Parameters.AddWithValue("at", link.SourceAssertedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => source.DisposeAsync();
}
