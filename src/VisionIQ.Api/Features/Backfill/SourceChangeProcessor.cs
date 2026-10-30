using Npgsql;
using VisionIQ.Api.Configuration;

namespace VisionIQ.Api.Features.Backfill;

/// <summary>
/// Minimal trusted source-change notification. The handler must re-read source data;
/// webhook bodies are not evidence and must not be indexed or archived as-is.
/// </summary>
public sealed record SourceChangeNotification(
    string SourceId,
    string EventId,
    string ProviderKind,
    string NativeId,
    string SourceRevision)
{
    /// <summary>Validates bounded, nonempty identifiers before any database interaction.</summary>
    public void Validate()
    {
        if (new[] { SourceId, EventId, ProviderKind, NativeId, SourceRevision }
                .Any(string.IsNullOrWhiteSpace) ||
            SourceId.Length > 256 || EventId.Length > 256 ||
            ProviderKind.Length > 80 || NativeId.Length > 256 ||
            SourceRevision.Length > 256)
        {
            throw new ArgumentException("Source-change notification identity is incomplete or oversized.");
        }
    }
}

/// <summary>Outcome of attempting to claim one at-least-once source event.</summary>
public enum ChangeClaimStatus
{
    Claimed,
    AlreadyCompleted,
    InProgress,
    IdentityConflict,
}

/// <summary>Processor result; a quarantined event requires source reconciliation.</summary>
public enum ChangeProcessingStatus
{
    Completed,
    AlreadyCompleted,
    InProgress,
    IdentityConflict,
    Quarantined,
}

/// <summary>An acquired lease token; only its owner can complete this attempt.</summary>
public sealed record ChangeClaim(ChangeClaimStatus Status, Guid? Token = null);

/// <summary>Persistence boundary for deduplication, retries and quarantine.</summary>
public interface ISourceChangeInbox
{
    /// <summary>Claims an event, detecting conflicting replay and active worker leases.</summary>
    Task<ChangeClaim> TryClaimAsync(
        SourceChangeNotification change, CancellationToken cancellationToken = default);

    /// <summary>Completes the current processing attempt after source re-fetch succeeds.</summary>
    Task CompleteAsync(
        SourceChangeNotification change, Guid token, CancellationToken cancellationToken = default);

    /// <summary>Quarantines the current attempt for explicit reconciliation and retry.</summary>
    Task QuarantineAsync(
        SourceChangeNotification change, Guid token, string code,
        CancellationToken cancellationToken = default);
}

/// <summary>Provider-specific handler that re-fetches and stores the authenticated source revision.</summary>
public interface ISourceChangeHandler
{
    /// <summary>Re-fetches the provider record; implementations must be idempotent.</summary>
    Task ApplyAsync(
        SourceChangeNotification change, CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs trusted event processing without exposing an unauthenticated Pub/Sub endpoint.
/// Transport authentication and subscription wiring belong to FND-06.
/// </summary>
public sealed class SourceChangeProcessor(
    ISourceChangeInbox inbox,
    ISourceChangeHandler handler)
{
    /// <summary>
    /// Claims, re-fetches and persists one event; failures are recorded as quarantine,
    /// not represented as a successful empty sync. Cancellation propagates for retry.
    /// </summary>
    public async Task<ChangeProcessingStatus> ProcessAsync(
        SourceChangeNotification change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        change.Validate();
        var claim = await inbox.TryClaimAsync(change, cancellationToken);
        if (claim.Status != ChangeClaimStatus.Claimed)
        {
            return claim.Status switch
            {
                ChangeClaimStatus.AlreadyCompleted => ChangeProcessingStatus.AlreadyCompleted,
                ChangeClaimStatus.InProgress => ChangeProcessingStatus.InProgress,
                ChangeClaimStatus.IdentityConflict => ChangeProcessingStatus.IdentityConflict,
                _ => throw new InvalidOperationException("Unknown source event claim state."),
            };
        }

        var token = claim.Token ?? throw new InvalidOperationException("Claimed event has no lease token.");
        try
        {
            await handler.ApplyAsync(change, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The claim expires and redelivery safely retries the operation.
            throw;
        }
        catch (Exception)
        {
            // Do not put exception text or source payload into database diagnostics.
            await inbox.QuarantineAsync(change, token, "SOURCE_APPLY_FAILED", cancellationToken);
            return ChangeProcessingStatus.Quarantined;
        }

        await inbox.CompleteAsync(change, token, cancellationToken);
        return ChangeProcessingStatus.Completed;
    }
}

/// <summary>
/// PostgreSQL-backed claim/lease implementation for at-least-once notifications.
/// All updates are restricted to the existing evidence schema and API runtime role.
/// </summary>
public sealed class PostgresSourceChangeInbox : ISourceChangeInbox, IAsyncDisposable
{
    private readonly NpgsqlDataSource source;
    private readonly TimeSpan lease;

    /// <summary>Creates a pooled runtime data source with a bounded worker lease.</summary>
    public PostgresSourceChangeInbox(DatabaseOptions options, TimeSpan? leaseDuration = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        lease = leaseDuration ?? TimeSpan.FromMinutes(5);
        if (lease < TimeSpan.FromSeconds(10) || lease > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = options.Host,
            Port = options.Port,
            Database = options.Name,
            Username = options.User,
            Password = options.Password,
            Timeout = 10,
        };
        connection["SSL Mode"] = options.SslMode;
        source = NpgsqlDataSource.Create(connection.ConnectionString);
    }

    /// <summary>
    /// Atomically inserts or takes over an expired/quarantined event lease,
    /// rejecting same-event-ID/different-source-item conflicts.
    /// </summary>
    public async Task<ChangeClaim> TryClaimAsync(
        SourceChangeNotification change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        change.Validate();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var token = Guid.NewGuid();

        await using var insert = new NpgsqlCommand("""
            INSERT INTO evidence.source_change_inbox (
                source_id, event_id, provider_kind, native_id, source_revision,
                claim_token, claim_expires_at
            ) VALUES (
                @source, @event, @kind, @native, @revision,
                @token, now() + @duration
            )
            ON CONFLICT (source_id, event_id) DO NOTHING
            RETURNING event_id
            """, connection, transaction);
        AddSourceParameters(insert, change);
        insert.Parameters.AddWithValue("token", token);
        insert.Parameters.AddWithValue("duration", lease);
        if (await insert.ExecuteScalarAsync(cancellationToken) is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new ChangeClaim(ChangeClaimStatus.Claimed, token);
        }

        await using var lookup = new NpgsqlCommand("""
            SELECT provider_kind, native_id, source_revision, status, claim_expires_at
            FROM evidence.source_change_inbox
            WHERE source_id = @source AND event_id = @event FOR UPDATE
            """, connection, transaction);
        lookup.Parameters.AddWithValue("source", change.SourceId);
        lookup.Parameters.AddWithValue("event", change.EventId);
        await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The claimed event vanished during the transaction.");
        }

        var same = string.Equals(reader.GetString(0), change.ProviderKind, StringComparison.Ordinal) &&
                   string.Equals(reader.GetString(1), change.NativeId, StringComparison.Ordinal) &&
                   string.Equals(reader.GetString(2), change.SourceRevision, StringComparison.Ordinal);
        var status = reader.GetString(3);
        var expired = reader.GetFieldValue<DateTimeOffset>(4) <= DateTimeOffset.UtcNow;
        await reader.DisposeAsync();

        if (!same)
        {
            return new ChangeClaim(ChangeClaimStatus.IdentityConflict);
        }

        if (status == "completed")
        {
            return new ChangeClaim(ChangeClaimStatus.AlreadyCompleted);
        }

        if (status == "processing" && !expired)
        {
            return new ChangeClaim(ChangeClaimStatus.InProgress);
        }

        await using var update = new NpgsqlCommand("""
            UPDATE evidence.source_change_inbox SET
                status = 'processing', claim_token = @token,
                claim_expires_at = now() + @duration,
                attempt_count = attempt_count + 1,
                updated_at = now(), last_error_code = NULL
            WHERE source_id = @source AND event_id = @event
            """, connection, transaction);
        update.Parameters.AddWithValue("source", change.SourceId);
        update.Parameters.AddWithValue("event", change.EventId);
        update.Parameters.AddWithValue("token", token);
        update.Parameters.AddWithValue("duration", lease);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ChangeClaim(ChangeClaimStatus.Claimed, token);
    }

    /// <summary>Marks only the current lease token completed, never a stale worker claim.</summary>
    public Task CompleteAsync(
        SourceChangeNotification change, Guid token, CancellationToken cancellationToken = default)
        => ChangeStatusAsync(change, token, "completed", null, cancellationToken);

    /// <summary>Marks the failed claim as quarantined using sanitized machine-readable error codes.</summary>
    public Task QuarantineAsync(
        SourceChangeNotification change, Guid token, string code,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 80 ||
            code.Any(character => character is not (>= 'A' and <= 'Z' or '_')))
        {
            throw new ArgumentException("Quarantine code must contain only uppercase ASCII and underscores.");
        }

        return ChangeStatusAsync(change, token, "quarantined", code, cancellationToken);
    }

    /// <summary>Updates a claimed event only if the supplied lease is still current.</summary>
    private async Task ChangeStatusAsync(
        SourceChangeNotification change, Guid token, string status, string? errorCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        change.Validate();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE evidence.source_change_inbox SET
                status = @status, last_error_code = @error,
                updated_at = now(),
                completed_at = CASE WHEN @status = 'completed' THEN now() ELSE completed_at END
            WHERE source_id = @source AND event_id = @event
              AND claim_token = @token AND status = 'processing'
            """, connection);
        command.Parameters.AddWithValue("source", change.SourceId);
        command.Parameters.AddWithValue("event", change.EventId);
        command.Parameters.AddWithValue("token", token);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("error", (object?)errorCode ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Source event lease was replaced before completion.");
        }
    }

    /// <summary>Attaches validated immutable source-identity parameters to a SQL command.</summary>
    private static void AddSourceParameters(
        NpgsqlCommand command, SourceChangeNotification change)
    {
        command.Parameters.AddWithValue("source", change.SourceId);
        command.Parameters.AddWithValue("event", change.EventId);
        command.Parameters.AddWithValue("kind", change.ProviderKind);
        command.Parameters.AddWithValue("native", change.NativeId);
        command.Parameters.AddWithValue("revision", change.SourceRevision);
    }

    /// <summary>Disposes the Npgsql data source and its connection pool.</summary>
    public ValueTask DisposeAsync() => source.DisposeAsync();
}
