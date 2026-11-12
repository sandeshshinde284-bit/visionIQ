using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using VisionIQ.Api.Configuration;
using VisionIQ.Api.Features.Briefs;
using VisionIQ.Api.Features.Events;
using VisionIQ.Api.Features.Feedback;
using static VisionIQ.Api.Tests.PickupTestData;

namespace VisionIQ.Api.Tests;

/// <summary>
/// BRF-05 / BRF-06 / BRF-07 completion evidence on disposable PostgreSQL (after <c>python -m db.manage all</c>):
/// a completed run gets exactly one publication with its pickup-to-comment time, and a rating is stored against
/// the advice id with who and when. Nothing here contacts Azure DevOps: delivery runs in record mode.
/// Needs DB_API_PASSWORD and, to store the brief as Intelligence would, DB_INTELLIGENCE_USER / _PASSWORD.
/// </summary>
public sealed class BriefDeliveryDatabaseIntegrationTests
{
    private const string Flag = "VISIONIQ_BRIEF_DB_INTEGRATION";
    private const string Trace = "0af7651916cd43dd8448eb211c80319c";
    private static readonly BriefPublishOptions Record = new(BriefPublishMode.Record, null, null, TimeSpan.FromSeconds(2));

    [DatabaseFact(Flag)]
    public async Task A_completed_run_is_published_once_and_its_advice_is_rated_inside_the_ticket()
    {
        await using var api = NpgsqlDataSource.Create(Connection("DB_API_USER", "DB_API_PASSWORD", "visioniq_api"));
        var tenant = "brief-test-" + Guid.NewGuid().ToString("N");
        var ticket = Random.Shared.Next(100_000, int.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (runId, resultId) = (Guid.NewGuid(), Guid.NewGuid());
        await CreateRunAsync(api, tenant, runId, ticket);
        await StoreBriefAsIntelligenceAsync(tenant, runId, resultId, ticket);

        // The result arrives; redelivery of the same event changes nothing.
        var completed = Completed(tenant, runId, resultId, ResultState.Published, ["ADV-001", "ADV-002"]);
        var inbox = new PostgresInboundEventProcessor(api, NullLogger<PostgresInboundEventProcessor>.Instance, Record);
        Assert.Equal(InboundOutcome.Processed, await inbox.ProcessAsync(completed, default));
        Assert.Equal(InboundOutcome.Duplicate, await inbox.ProcessAsync(completed, default));

        var publisher = new BriefPublisher(
            new PostgresBriefPublicationStore(api), new RecordingCommentPublisher(), Policy, NullLogger<BriefPublisher>.Instance);
        while (await publisher.ProcessPendingAsync(batchSize: 100) > 0)
        {
        }

        await using var connection = await api.OpenConnectionAsync();
        await using (var publication = new NpgsqlCommand(
            "SELECT status, mode, comment_id, comment_body, time_to_brief_ms, attempt_count, " +
            "       published_at >= picked_up_at, (SELECT count(*) FROM workflow.brief_publications WHERE run_id = $1) " +
            "FROM workflow.brief_publications WHERE run_id = $1",
            connection))
        {
            publication.Parameters.Add(new NpgsqlParameter { Value = runId });
            await using var reader = await publication.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(("published", "record", "recorded", 1, true, 1L),
                (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(5), reader.GetBoolean(6), reader.GetInt64(7)));
            var comment = reader.GetString(3);
            Assert.Contains("<b>ADV-001</b>", comment, StringComparison.Ordinal);
            Assert.Contains(BriefCommentRenderer.Marker(runId), comment, StringComparison.Ordinal);
            Assert.True(reader.GetInt64(4) >= 0); // BRF-07: pickup-to-comment time is recorded for the run
        }

        // BRF-06: ratings from ticket comments, stored against the advice id with who and when.
        var feedback = new PostgresAdviceFeedbackStore(api);
        var ratedAt = new DateTimeOffset(2026, 11, 10, 9, 30, 0, TimeSpan.Zero);
        FeedbackSubmission Comment(string delivery, params AdviceRating[] ratings) =>
            // ADO event ids are unique; prefix them so the test can run again on the same database.
            new(tenant, "payments-core", ticket, tenant + "-" + delivery, "a1b2c3", ratedAt, ratings);

        var first = await feedback.RecordAsync(
            Comment("delivery-1", new("ADV-001", AdviceDisposition.Accepted, null), new("ADV-009", AdviceDisposition.Rejected, "typo")),
            default);
        var redelivered = await feedback.RecordAsync(Comment("delivery-1", new AdviceRating("ADV-001", AdviceDisposition.Accepted, null)), default);
        var changedMind = await feedback.RecordAsync(
            Comment("delivery-2", new AdviceRating("ADV-001", AdviceDisposition.Modified, "added a dedupe key")), default);
        var unknownOnly = await feedback.RecordAsync(Comment("delivery-3", new AdviceRating("ADV-009", AdviceDisposition.Accepted, null)), default);
        var otherTicket = await feedback.RecordAsync(
            Comment("delivery-4", new AdviceRating("ADV-001", AdviceDisposition.Accepted, null)) with { TicketId = "1" }, default);

        Assert.Equal(("recorded", "ADV-001", "ADV-009", runId), (first.Status, Assert.Single(first.Recorded), Assert.Single(first.Unknown), first.RunId));
        Assert.Equal("duplicate", redelivered.Status);
        Assert.Equal("recorded", changedMind.Status);
        Assert.Equal(("ignored", "unknown_advice_ids"), (unknownOnly.Status, unknownOnly.Reason));
        Assert.Equal(("ignored", "no_published_brief"), (otherTicket.Status, otherTicket.Reason));

        // Append-only: both decisions are kept in order, each tied to the immutable brief revision.
        await using (var history = new NpgsqlCommand(
            "SELECT string_agg(disposition, ',' ORDER BY received_at, disposition), count(DISTINCT result_id), " +
            "       min(reviewer_id), min(rated_at), max(note) " +
            "FROM workflow.advice_feedback WHERE run_id = $1 AND advice_id = 'ADV-001'",
            connection))
        {
            history.Parameters.Add(new NpgsqlParameter { Value = runId });
            await using var reader = await history.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(("ACCEPTED,MODIFIED", 1L, "a1b2c3", ratedAt, "added a dedupe key"),
                (reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4)));
        }

        await using var tamper = new NpgsqlCommand(
            "UPDATE workflow.advice_feedback SET disposition = 'REJECTED' WHERE run_id = $1", connection);
        tamper.Parameters.Add(new NpgsqlParameter { Value = runId });
        var refused = await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
        Assert.Contains("append-only", refused.MessageText, StringComparison.Ordinal);
    }

    [DatabaseFact(Flag)]
    public async Task Nothing_is_queued_when_publishing_is_off_or_the_pipeline_is_disabled()
    {
        await using var api = NpgsqlDataSource.Create(Connection("DB_API_USER", "DB_API_PASSWORD", "visioniq_api"));
        var tenant = "brief-test-" + Guid.NewGuid().ToString("N");
        var (off, disabled) = (Guid.NewGuid(), Guid.NewGuid());
        await CreateRunAsync(api, tenant, off, "9001");
        await CreateRunAsync(api, tenant, disabled, "9002");

        await new PostgresInboundEventProcessor(api, NullLogger<PostgresInboundEventProcessor>.Instance, BriefPublishOptions.Disabled)
            .ProcessAsync(Completed(tenant, off, Guid.NewGuid(), ResultState.Published, ["ADV-001"]), default);
        await new PostgresInboundEventProcessor(api, NullLogger<PostgresInboundEventProcessor>.Instance, Record)
            .ProcessAsync(Completed(tenant, disabled, Guid.NewGuid(), ResultState.Blocked, [], "analysis_pipeline_disabled"), default);

        await using var connection = await api.OpenConnectionAsync();
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM workflow.brief_publications WHERE run_id = ANY($1)", connection);
        count.Parameters.Add(new NpgsqlParameter { Value = new[] { off, disabled } });
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    private static EventEnvelope Completed(
        string tenant, Guid runId, Guid resultId, ResultState state, string[] adviceIds, params string[] reasons) =>
        EventEnvelope.Create(
            EventContracts.AnalysisCompleted, tenant, runId.ToString(), Trace, runId, "7",
            new AnalysisCompletedV1(resultId, state, ["E1", "E2"], adviceIds, reasons)) with
        {
            Producer = "VisionIQ.Intelligence",
        };

    private static async Task CreateRunAsync(NpgsqlDataSource api, string tenant, Guid runId, string ticket)
    {
        await using var connection = await api.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var request = new AnalysisRequestedV1("payments-core", ticket, 7, "payments-api", Sha, AnalysisMode.Brief, "project:payments-core");
        Assert.NotNull(await AnalysisRuns.CreateAsync(connection, transaction, new NewAnalysisRun(runId, tenant, request, Trace)));
        await transaction.CommitAsync();
    }

    /// <summary>What the Python analysis worker writes: the accepted request and its immutable brief.</summary>
    private static async Task StoreBriefAsIntelligenceAsync(string tenant, Guid runId, Guid resultId, string ticket)
    {
        var document = BriefTestData.Brief();
        document["brief_id"] = resultId.ToString();
        document["run_id"] = runId.ToString();
        await using var intelligence = NpgsqlDataSource.Create(
            Connection("DB_INTELLIGENCE_USER", "DB_INTELLIGENCE_PASSWORD", "visioniq_intelligence"));
        await using var batch = intelligence.CreateBatch();
        var eventId = Guid.NewGuid();
        batch.BatchCommands.Add(new NpgsqlBatchCommand(
            "INSERT INTO knowledge.event_inbox (event_id, event_type, schema_version, trace_id) VALUES ($1, 'analysis.requested', 1, $2)")
        {
            Parameters = { new() { Value = eventId }, new() { Value = Trace } },
        });
        batch.BatchCommands.Add(new NpgsqlBatchCommand(
            "INSERT INTO knowledge.analysis_requests (run_id, event_id, tenant_id, project_id, ticket_id, ticket_revision, " +
            "repository_id, base_commit_sha, analysis_mode, security_scope_id, status, result_id, result_state, completed_at) " +
            "VALUES ($1, $2, $3, 'payments-core', $4, 7, 'payments-api', $5, 'brief', 'project:payments-core', 'completed', $6, 'PUBLISHED', now())")
        {
            Parameters =
            {
                new() { Value = runId }, new() { Value = eventId }, new() { Value = tenant }, new() { Value = ticket },
                new() { Value = Sha }, new() { Value = resultId },
            },
        });
        batch.BatchCommands.Add(new NpgsqlBatchCommand(
            "INSERT INTO knowledge.briefs (brief_id, run_id, tenant_id, project_id, ticket_id, ticket_revision, state, risk_band, " +
            "schema_version, document, routing_revision, accepted_at, retrieval_ms, generation_ms, total_ms) " +
            "VALUES ($1, $2, $3, 'payments-core', $4, 7, 'PUBLISHED', 'MODERATE', 1, $5::jsonb, 'routing-test', now(), 300, 9000, 9600)")
        {
            Parameters =
            {
                new() { Value = resultId }, new() { Value = runId }, new() { Value = tenant }, new() { Value = ticket },
                new() { Value = document.ToJsonString() },
            },
        });
        await batch.ExecuteNonQueryAsync();
    }

    private static string Connection(string userVariable, string passwordVariable, string defaultUser)
    {
        var password = Environment.GetEnvironmentVariable(passwordVariable);
        Assert.False(string.IsNullOrWhiteSpace(password), passwordVariable + " is required for this test");
        return new DatabaseOptions(
            Environment.GetEnvironmentVariable("DB_HOST") ?? "127.0.0.1",
            int.TryParse(Environment.GetEnvironmentVariable("DB_PORT"), out var port) ? port : 5432,
            Environment.GetEnvironmentVariable("DB_NAME") ?? "visioniq",
            Environment.GetEnvironmentVariable(userVariable) ?? defaultUser,
            password!,
            "disable").CreateConnectionStringBuilder().ConnectionString;
    }
}
