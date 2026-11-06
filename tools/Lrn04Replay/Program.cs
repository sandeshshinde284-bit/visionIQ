// LRN-04 *test-only* replay. Reads already exported PR JSONL and exercises the real
// TicketFixProjectionSink and PostgresTicketFixLinkStore. This is NOT a canonical
// evidence/archive integration test: its evidence sink is intentionally in memory.
// The source instance is isolated to a new .invalid URL for every run.
using System.Text.Json;
using Npgsql;
using VisionIQ.Api.Configuration;
using VisionIQ.Api.Features.TicketFixes;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Cli;
using VisionIQ.Ingestor.Sync;

var write = args.Length == 1 && args[0] == "--write-test-db";
if (args.Length != 0 && !write)
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/Lrn04Replay [-- --write-test-db]");
    return 1;
}

static string Required(string name) =>
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))
        ? Environment.GetEnvironmentVariable(name)!
        : throw new InvalidOperationException($"Set {name} in the current shell.");

try
{
    var path = Path.GetFullPath(Required("LRN04_REPLAY_JSONL"));
    var expectedProject = Required("LRN04_REPLAY_PROJECT");
    var expectedRepositoryId = Required("LRN04_REPLAY_REPO_ID");
    if (!File.Exists(path) || Path.GetFileName(path) != "pull-requests.jsonl")
        throw new InvalidOperationException("LRN04_REPLAY_JSONL must name an existing pull-requests.jsonl file.");

    var pairs = new HashSet<(string Bug, int Pr)>();
    foreach (var token in Required("LRN04_REPLAY_PAIRS").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
    {
        var parts = token.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var bug) || bug <= 0 ||
            !int.TryParse(parts[1], out var pr) || pr <= 0)
            throw new InvalidOperationException("LRN04_REPLAY_PAIRS must be bugId:prId;bugId:prId...");
        pairs.Add((bug.ToString(), pr));
    }
    if (pairs.Count != 7 || pairs.Select(x => x.Bug).Distinct().Count() != 5)
        throw new InvalidOperationException("This acceptance replay expects seven distinct pairs covering five distinct bugs.");

    // Validate every selected source relationship before opening a database connection.
    var records = new Dictionary<int, PullRequestRecord>();
    var serializer = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    foreach (var line in File.ReadLines(path))
    {
        using var envelope = JsonDocument.Parse(line);
        if (!envelope.RootElement.TryGetProperty("record", out var recordNode))
            throw new InvalidOperationException("JSONL entry has no record field.");
        if (!recordNode.TryGetProperty("id", out var idNode) || !idNode.TryGetInt32(out var id))
            continue;
        if (!pairs.Any(x => x.Pr == id)) continue;
        var pr = recordNode.Deserialize<PullRequestRecord>(serializer)
            ?? throw new InvalidOperationException("Cannot deserialize selected PR record.");
        if (!string.Equals(pr.Project, expectedProject, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pr.RepositoryId, expectedRepositoryId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pr.Status, "completed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"PR {id}: project, repository, or status differs from expected scope.");
        if (pr.CommitIds.Any(s => string.IsNullOrWhiteSpace(s) || s.Length != 40 || !s.All(Uri.IsHexDigit)))
            throw new InvalidOperationException($"PR {id}: at least one commit ID is not a full SHA.");
        records[id] = pr;
    }
    foreach (var pair in pairs)
    {
        if (!records.TryGetValue(pair.Pr, out var pr) ||
            !pr.WorkItemIds.Any(x => x.ToString() == pair.Bug))
            throw new InvalidOperationException($"Required explicit source association missing: {pair.Bug} -> {pair.Pr}.");
    }
    Console.WriteLine($"PASS: {pairs.Count} ADO-declared associations from {records.Count} completed PRs; {pairs.Select(x => x.Bug).Distinct().Count()} distinct bugs.");
    if (!write)
    {
        Console.WriteLine("DRY RUN: no database connections or writes. Use --write-test-db for isolated persistence verification.");
        return 0;
    }

    // Strict fail-closed database guard. No connection or write before this passes.
    var host = Required("DB_HOST");
    var port = Required("DB_PORT");
    var database = Required("DB_NAME");
    var user = Required("DB_API_USER");
    var password = Required("DB_API_PASSWORD");
    if (host is not ("127.0.0.1" or "localhost") ||
        port != "55433" || database != "visioniq_lrn03_test" ||
        user != "visioniq_lrn03_api")
        throw new InvalidOperationException("SAFETY STOP: allowed target is localhost:55433/visioniq_lrn03_test as visioniq_lrn03_api only.");

    var dbOptions = new DatabaseOptions(host, int.Parse(port), database, user, password, "disable");
    var builder = new NpgsqlConnectionStringBuilder
    {
        Host = host, Port = int.Parse(port), Database = database,
        Username = user, Password = password, SslMode = SslMode.Disable,
    };
    var testSource = new Uri($"https://lrn04-replay-{Guid.NewGuid():N}.invalid/");
    var sourceScope = testSource.AbsoluteUri.TrimEnd('/');
    var cleaned = false;
    try
    {
        await using var store = new PostgresTicketFixLinkStore(dbOptions);
        var evidence = new InMemoryReplayEvidenceSink();
        var projector = new TicketFixProjectionSink(evidence, store, testSource);
        foreach (var pr in records.Values.OrderBy(p => p.Id))
            await projector.WriteAsync("pull-requests", $"local-replay/pr/{pr.Id}", pr);
        var firstCount = await VerifyRowsAsync(builder.ConnectionString, sourceScope, pairs);
        foreach (var pr in records.Values.OrderBy(p => p.Id))
            await projector.WriteAsync("pull-requests", $"local-replay/pr/{pr.Id}", pr);
        var secondCount = await VerifyRowsAsync(builder.ConnectionString, sourceScope, pairs);
        if (firstCount != secondCount)
            throw new InvalidOperationException($"Idempotence failed: row count changed {firstCount} -> {secondCount}.");
        Console.WriteLine($"PASS: persisted {firstCount} scoped association rows; all seven expected pairs are explicit, unreviewed and evidence-referenced.");
        Console.WriteLine("PASS: replay did not duplicate rows.");
    }
    finally
    {
        // Delete only this run's unique source instance. Existing test rows are untouched.
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM evidence.ticket_fix_reviews WHERE association_key IN " +
            "(SELECT association_key FROM evidence.ticket_fix_links WHERE source_instance=@source)", connection, transaction))
        {
            cmd.Parameters.AddWithValue("source", sourceScope);
            await cmd.ExecuteNonQueryAsync();
        }
        foreach (var table in new[] { "ticket_fix_links", "ticket_fix_revert_observations" })
        {
            await using var cmd = new NpgsqlCommand($"DELETE FROM evidence.{table} WHERE source_instance=@source", connection, transaction);
            cmd.Parameters.AddWithValue("source", sourceScope);
            await cmd.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        cleaned = true;
        Console.WriteLine("CLEANUP: test-only rows removed; previously existing associations were not targeted.");
    }
    return cleaned ? 0 : 2;
}
catch (Exception e)
{
    Console.Error.WriteLine($"FAIL ({e.GetType().Name}): {e.Message}");
    return 1;
}

static async Task<long> VerifyRowsAsync(
    string connectionString, string sourceScope, HashSet<(string Bug, int Pr)> pairs)
{
    await using var db = new NpgsqlConnection(connectionString);
    await db.OpenAsync();
    await using var cmd = new NpgsqlCommand("""
        SELECT ticket_id, pull_request_id, source_classification, review_status,
               jsonb_array_length(source_evidence_refs) > 0 AS has_evidence
        FROM evidence.ticket_fix_links
        WHERE source_instance=@source
        """, db);
    cmd.Parameters.AddWithValue("source", sourceScope);
    await using var rows = await cmd.ExecuteReaderAsync();
    var seen = new HashSet<(string Bug, int Pr)>();
    long count = 0;
    while (await rows.ReadAsync())
    {
        count++;
        var bug = rows.GetString(0);
        var prText = rows.IsDBNull(1) ? "" : rows.GetString(1);
        if (!int.TryParse(prText, out var pr))
            throw new InvalidOperationException("Unexpected unscoped PR association.");
        // Newly ingested explicit ADO associations have no human review yet.
        // review_status is nullable; NULL is the only valid initial state here.
        if (rows.GetString(2) != "explicit" ||
            !rows.IsDBNull(3) ||
            !rows.GetBoolean(4))
        {
            throw new InvalidOperationException($"Invalid provenance or review status for {bug} -> {pr}.");
        }
        seen.Add((bug, pr));
    }
    foreach (var pair in pairs)
        if (!seen.Contains(pair))
            throw new InvalidOperationException($"Database is missing expected pair {pair.Bug} -> {pair.Pr}.");
    return count;
}

sealed class InMemoryReplayEvidenceSink : ICanonicalEvidenceSink
{
    public Task WriteAsync(string stream, string key, object record,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
