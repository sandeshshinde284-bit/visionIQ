using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using VisionIQ.Api.Configuration;
using VisionIQ.Api.Features.Briefs;
using VisionIQ.Api.Features.Events;
using VisionIQ.Api.Features.Fixes;
using static VisionIQ.Api.Tests.PickupTestData;

namespace VisionIQ.Api.Tests;

/// <summary>
/// DEF-05 / DEF-06 on disposable PostgreSQL (after <c>python -m db.manage all</c>): the SQL of
/// <see cref="PostgresFixPullRequestStore"/>, <see cref="FixRunStarter"/> and the deep branch of the inbox, which
/// the other fix tests replace with fakes. Nothing here contacts Azure DevOps: delivery runs in record mode.
/// Needs DB_API_PASSWORD and, to store the fix result as Intelligence would, DB_INTELLIGENCE_USER / _PASSWORD.
/// </summary>
public sealed class FixDeliveryDatabaseIntegrationTests
{
    private const string Flag = "VISIONIQ_FIX_DB_INTEGRATION";
    private const string Trace = "0af7651916cd43dd8448eb211c80319c";
    private static readonly BriefPublishOptions Briefs = new(BriefPublishMode.Record, null, null, TimeSpan.FromSeconds(2));
    private static readonly FixPullRequestOptions Record = new(FixPullRequestMode.Record, null, null, TimeSpan.FromSeconds(2), false);
    private static readonly FixPullRequestOptions AutoStart = Record with { AutoStart = true };

    [DatabaseFact(Flag)]
    public async Task A_verified_fix_is_recorded_as_one_draft_pull_request_and_never_as_a_ticket_comment()
    {
        await using var api = NpgsqlDataSource.Create(Connection("DB_API_USER", "DB_API_PASSWORD", "visioniq_api"));
        var tenant = "fix-test-" + Guid.NewGuid().ToString("N");
        var (runId, resultId) = (Guid.NewGuid(), Guid.NewGuid());
        // The run the API recorded and the result Intelligence stored are about the same ticket and baseline.
        await CreateRunAsync(api, tenant, runId, FixTestData.Verified.Ticket.Id, AnalysisMode.Deep, baseSha: FixTestData.Verified.BaseSha);
        var fix = await StoreFixAsIntelligenceAsync(tenant, runId, resultId, FixTestData.VerifiedJson);
        // A second run whose stored result names another baseline than the run was started with.
        var (otherRun, otherResult) = (Guid.NewGuid(), Guid.NewGuid());
        await CreateRunAsync(api, tenant + "-b", otherRun, FixTestData.Verified.Ticket.Id, AnalysisMode.Deep);
        await StoreFixAsIntelligenceAsync(tenant + "-b", otherRun, otherResult, FixTestData.VerifiedJson);

        // The result arrives; redelivery of the same event changes nothing.
        var completed = Completed(tenant, runId, resultId, ResultState.Published);
        var inbox = new PostgresInboundEventProcessor(api, NullLogger<PostgresInboundEventProcessor>.Instance, Briefs, Record);
        Assert.Equal(InboundOutcome.Processed, await inbox.ProcessAsync(completed, default));
        Assert.Equal(InboundOutcome.Duplicate, await inbox.ProcessAsync(completed, default));
        await inbox.ProcessAsync(Completed(tenant + "-b", otherRun, otherResult, ResultState.Published), default);

        var store = new PostgresFixPullRequestStore(api);
        var publisher = new FixPullRequestPublisher(
            store, new RecordingPullRequestPublisher(), Policy, NullLogger<FixPullRequestPublisher>.Instance);
        while (await publisher.ProcessPendingAsync(batchSize: 100) > 0)
        {
        }

        await using var connection = await api.OpenConnectionAsync();
        await using (var delivery = new NpgsqlCommand(
            "SELECT status, mode, pull_request_id, repository_id, source_branch, verified_head_sha, current_head_sha, " +
            "       jsonb_array_length(checks), attempt_count, request_body, " +
            "       (SELECT count(*) FROM workflow.fix_pull_requests WHERE run_id = $1), " +
            "       (SELECT count(*) FROM workflow.brief_publications WHERE run_id = $1) " +
            "FROM workflow.fix_pull_requests WHERE run_id = $1",
            connection))
        {
            delivery.Parameters.Add(new NpgsqlParameter { Value = runId });
            await using var reader = await delivery.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(("published", "record", "recorded", "kiosk-demo"),
                (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            Assert.StartsWith("visioniq/fix/", reader.GetString(4), StringComparison.Ordinal);
            // Nothing was pushed, so the results apply to the sandbox candidate itself.
            Assert.Equal(fix["candidate_sha"]!.GetValue<string>(), reader.GetString(5));
            Assert.Equal(reader.GetString(5), reader.GetString(6));
            Assert.Equal(6, reader.GetInt32(7)); // one status per sandbox gate
            Assert.Equal(1, reader.GetInt32(8));
            Assert.Contains("\"is_draft\":true", reader.GetString(9), StringComparison.Ordinal);
            Assert.Equal((1L, 0L), (reader.GetInt64(10), reader.GetInt64(11)));
        }

        // The result that does not match its run is never delivered, in any mode.
        await using (var mismatch = new NpgsqlCommand(
            "SELECT status, reason, pull_request_id IS NULL FROM workflow.fix_pull_requests WHERE run_id = $1", connection))
        {
            mismatch.Parameters.Add(new NpgsqlParameter { Value = otherRun });
            await using var reader = await mismatch.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(("failed", "fix_result_does_not_match_run", true), (reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
        }

        // A recorded pull request is not on Azure DevOps, so a "pull request updated" delivery never matches it.
        Assert.Null(await store.FindOpenedAsync("recorded", ["kiosk-demo"], default));
    }

    [DatabaseFact(Flag)]
    public async Task A_result_without_a_verified_fix_opens_nothing_and_says_why()
    {
        await using var api = NpgsqlDataSource.Create(Connection("DB_API_USER", "DB_API_PASSWORD", "visioniq_api"));
        var tenant = "fix-test-" + Guid.NewGuid().ToString("N");
        var (blocked, missing, off) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var blockedResult = Guid.NewGuid();
        foreach (var run in new[] { blocked, missing, off })
        {
            await CreateRunAsync(api, tenant, run, NewTicket(), AnalysisMode.Deep);
        }

        await StoreFixAsIntelligenceAsync(tenant, blocked, blockedResult, FixTestData.BlockedJson);
        var inbox = new PostgresInboundEventProcessor(api, NullLogger<PostgresInboundEventProcessor>.Instance, Briefs, Record);
        await inbox.ProcessAsync(Completed(tenant, blocked, blockedResult, ResultState.Blocked, "reproduction_not_red"), default);
        // The event names a result Intelligence never stored.
        await inbox.ProcessAsync(Completed(tenant, missing, Guid.NewGuid(), ResultState.Published), default);
        // FIX_PR_MODE=off: the result is stored, nothing is queued.
        await new PostgresInboundEventProcessor(api, NullLogger<PostgresInboundEventProcessor>.Instance, Briefs)
            .ProcessAsync(Completed(tenant, off, Guid.NewGuid(), ResultState.Published), default);

        var publisher = new FixPullRequestPublisher(
            new PostgresFixPullRequestStore(api), new RecordingPullRequestPublisher(), Policy,
            NullLogger<FixPullRequestPublisher>.Instance);
        while (await publisher.ProcessPendingAsync(batchSize: 100) > 0)
        {
        }

        var rows = new Dictionary<Guid, (string Status, string? Reason, bool HasPullRequest)>();
        await using var connection = await api.OpenConnectionAsync();
        await using var deliveries = new NpgsqlCommand(
            "SELECT run_id, status, reason, pull_request_id IS NOT NULL FROM workflow.fix_pull_requests WHERE run_id = ANY($1)",
            connection);
        deliveries.Parameters.Add(new NpgsqlParameter { Value = new[] { blocked, missing, off } });
        await using var reader = await deliveries.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows[reader.GetGuid(0)] = (reader.GetString(1), await reader.IsDBNullAsync(2) ? null : reader.GetString(2), reader.GetBoolean(3));
        }

        Assert.Equal(("skipped", "no_draft_pull_request", false), rows[blocked]);
        Assert.False(rows.ContainsKey(off));
        Assert.NotEqual("published", rows[missing].Status);
        Assert.False(rows[missing].HasPullRequest);
    }

    [DatabaseFact(Flag)]
    public async Task A_defect_brief_starts_exactly_one_fix_run_and_a_fix_result_starts_none()
    {
        await using var api = NpgsqlDataSource.Create(Connection("DB_API_USER", "DB_API_PASSWORD", "visioniq_api"));
        var tenant = "fix-test-" + Guid.NewGuid().ToString("N");
        var (defect, enhancement, blockedBrief) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var defectTicket = NewTicket();
        await CreateRunAsync(api, tenant, defect, defectTicket, AnalysisMode.Brief, "defect");
        await CreateRunAsync(api, tenant, enhancement, NewTicket(), AnalysisMode.Brief, "enhancement");
        await CreateRunAsync(api, tenant, blockedBrief, NewTicket(), AnalysisMode.Brief, "defect");

        var inbox = new PostgresInboundEventProcessor(api, NullLogger<PostgresInboundEventProcessor>.Instance, Briefs, AutoStart);
        var brief = Completed(tenant, defect, Guid.NewGuid(), ResultState.Published);
        Assert.Equal(InboundOutcome.Processed, await inbox.ProcessAsync(brief, default));
        Assert.Equal(InboundOutcome.Duplicate, await inbox.ProcessAsync(brief, default));
        await inbox.ProcessAsync(Completed(tenant, enhancement, Guid.NewGuid(), ResultState.Published), default);
        await inbox.ProcessAsync(Completed(tenant, blockedBrief, Guid.NewGuid(), ResultState.Blocked, "ticket_not_readable"), default);

        var fixRuns = await DeepRunsAsync(api, tenant);
        var (fixRun, ticket, baseSha, policyRevision, requested) = Assert.Single(fixRuns);
        Assert.Equal((defectTicket, Sha, "test-policy.fix", 1L), (ticket, baseSha, policyRevision, requested));

        // The fix run's own result is queued for a pull request and starts no further run.
        await inbox.ProcessAsync(Completed(tenant, fixRun, Guid.NewGuid(), ResultState.Partial, "fix_budget_exhausted"), default);
        Assert.Single(await DeepRunsAsync(api, tenant));
    }

    private static string NewTicket() =>
        Random.Shared.Next(100_000, int.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The tenant's deep runs with how many analysis.requested events were queued for each.</summary>
    private static async Task<List<(Guid RunId, string Ticket, string BaseSha, string PolicyRevision, long Requested)>> DeepRunsAsync(
        NpgsqlDataSource api, string tenant)
    {
        var runs = new List<(Guid, string, string, string, long)>();
        await using var connection = await api.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT r.run_id, r.ticket_id, r.base_commit_sha, r.policy_revision, " +
            "       (SELECT count(*) FROM workflow.event_outbox o WHERE o.event_type = 'analysis.requested' " +
            "         AND o.payload ->> 'run_id' = r.run_id::text AND o.payload -> 'data' ->> 'analysis_mode' = 'deep') " +
            "FROM workflow.analysis_runs r WHERE r.tenant_id = $1 AND r.analysis_mode = 'deep'",
            connection);
        command.Parameters.Add(new NpgsqlParameter { Value = tenant });
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            runs.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4)));
        }

        return runs;
    }

    private static EventEnvelope Completed(Guid runId, string tenant, Guid resultId, ResultState state, string[] reasons) =>
        EventEnvelope.Create(
            EventContracts.AnalysisCompleted, tenant, runId.ToString(), Trace, runId, "7",
            new AnalysisCompletedV1(resultId, state, ["X1"], [], reasons)) with
        {
            Producer = "VisionIQ.Intelligence",
        };

    private static EventEnvelope Completed(string tenant, Guid runId, Guid resultId, ResultState state, params string[] reasons) =>
        Completed(runId, tenant, resultId, state, reasons);

    private static async Task CreateRunAsync(
        NpgsqlDataSource api, string tenant, Guid runId, string ticket, AnalysisMode mode, string? ticketKind = null,
        string? baseSha = null)
    {
        await using var connection = await api.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var request = new AnalysisRequestedV1("payments-core", ticket, 7, "kiosk-demo", baseSha ?? Sha, mode, "project:payments-core");
        // A run started by ticket pickup has its kind and the policy revision; a fix run is only started for those.
        var run = ticketKind is null
            ? new NewAnalysisRun(runId, tenant, request, Trace)
            : new NewAnalysisRun(runId, tenant, request, Trace, "ado_webhook", ticketKind, "test-policy");
        Assert.NotNull(await AnalysisRuns.CreateAsync(connection, transaction, run));
        await transaction.CommitAsync();
    }

    /// <summary>What the Python fix worker writes: the accepted deep request and its immutable fix result.</summary>
    private static async Task<JsonNode> StoreFixAsIntelligenceAsync(string tenant, Guid runId, Guid resultId, string fixtureJson)
    {
        var document = JsonNode.Parse(fixtureJson)!;
        document["fix_id"] = resultId.ToString();
        document["run_id"] = runId.ToString();
        var state = document["state"]!.GetValue<string>();
        var reasons = document["reason_codes"]!.AsArray().Select(reason => reason!.GetValue<string>()).ToArray();
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
            "repository_id, base_commit_sha, analysis_mode, security_scope_id, status, result_id, result_state, reason_codes, completed_at) " +
            "VALUES ($1, $2, $3, 'payments-core', '4711', 7, 'kiosk-demo', $4, 'deep', 'project:payments-core', 'completed', $5, $6, $7, now())")
        {
            Parameters =
            {
                new() { Value = runId }, new() { Value = eventId }, new() { Value = tenant }, new() { Value = Sha },
                new() { Value = resultId }, new() { Value = state }, new() { Value = reasons },
            },
        });
        batch.BatchCommands.Add(new NpgsqlBatchCommand(
            "INSERT INTO knowledge.fix_runs (fix_id, run_id, tenant_id, project_id, ticket_id, ticket_revision, repository_id, " +
            "base_commit_sha, candidate_commit_sha, candidate_tree_sha, state, verdict, reason_codes, reproducibility, " +
            "gates_overall, schema_version, document, routing_revision, accepted_at, total_ms) " +
            "VALUES ($1, $2, $3, 'payments-core', '4711', 7, 'kiosk-demo', $4, $5, $6, $7, $8, $9, $10, $11, 1, $12::jsonb, " +
            "'routing-test', now(), 1000)")
        {
            Parameters =
            {
                new() { Value = resultId }, new() { Value = runId }, new() { Value = tenant },
                new() { Value = document["base_sha"]!.GetValue<string>() },
                new() { Value = (object?)document["candidate_sha"]?.GetValue<string>() ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text },
                new() { Value = (object?)document["patch"]?["tree_sha"]?.GetValue<string>() ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text },
                new() { Value = state }, new() { Value = document["verdict"]!.GetValue<string>() }, new() { Value = reasons },
                new() { Value = document["reproduction"]!["reproducibility"]!.GetValue<string>() },
                new() { Value = document["gate_summary"]!["overall"]!.GetValue<string>() },
                new() { Value = document.ToJsonString() },
            },
        });
        await batch.ExecuteNonQueryAsync();
        return document;
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
