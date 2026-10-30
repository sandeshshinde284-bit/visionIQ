using System.Data.Common;

namespace VisionIQ.Ingestor.Checkpoints;

/// <summary>
/// Checkpoints in evidence.connector_checkpoints (S04-14), written with the API database role.
/// The table comes from Alembic revision 20261030_0002.
/// </summary>
public sealed class DbCheckpointStore : ICheckpointStore
{
    public const string SelectSql =
        "SELECT cursor_value, updated_at FROM evidence.connector_checkpoints WHERE source_id = @source_id AND scope = @scope";

    public const string UpsertSql =
        "INSERT INTO evidence.connector_checkpoints (source_id, scope, cursor_value, updated_at) "
        + "VALUES (@source_id, @scope, @cursor_value, @updated_at) "
        + "ON CONFLICT (source_id, scope) DO UPDATE SET cursor_value = EXCLUDED.cursor_value, updated_at = EXCLUDED.updated_at";

    private readonly Func<DbConnection> connectionFactory;

    public DbCheckpointStore(Func<DbConnection> connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        this.connectionFactory = connectionFactory;
    }

    public async Task<SyncCheckpoint?> GetAsync(string sourceId, string scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        var connection = connectionFactory();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = SelectSql;
                AddParameter(command, "source_id", sourceId);
                AddParameter(command, "scope", scope);
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return null;
                    }

                    return new SyncCheckpoint(sourceId, scope, reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1));
                }
            }
        }
    }

    public async Task SaveAsync(SyncCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        var connection = connectionFactory();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = UpsertSql;
                AddParameter(command, "source_id", checkpoint.SourceId);
                AddParameter(command, "scope", checkpoint.Scope);
                AddParameter(command, "cursor_value", checkpoint.Cursor);
                AddParameter(command, "updated_at", checkpoint.UpdatedAt.ToUniversalTime());
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
