using VisionIQ.Ingestor.Diagnostics;

namespace VisionIQ.Ingestor.Tests;

/// <summary>Phase 2 instrumentation: per-record stage timings that never mix between parallel records.</summary>
public sealed class StageTimingsTests
{
    [Fact]
    public async Task Stages_and_counts_are_collected_per_record()
    {
        using var scope = StageTimings.Begin();
        using (StageTimings.Measure("gcs_archive"))
        {
            await Task.Delay(5);
        }

        using (StageTimings.Measure("gcs_archive"))
        {
        }

        StageTimings.Count("ticket_fix_claims", 12);

        var summary = scope.Timings.Summary();
        Assert.Matches(@"^gcs_archive=\d+ms/2 ticket_fix_claims=12$", summary);
        Assert.True(scope.Timings.Milliseconds("gcs_archive") >= 1);
    }

    [Fact]
    public void Outside_a_scope_nothing_is_recorded_and_nothing_fails()
    {
        using (StageTimings.Measure("pg_evidence"))
        {
        }

        StageTimings.Count("ticket_fix_claims", 3);
    }

    [Fact]
    public async Task Parallel_records_keep_their_own_timings()
    {
        var summaries = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
        await Parallel.ForEachAsync(Enumerable.Range(1, 20), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (record, token) =>
        {
            using var scope = StageTimings.Begin();
            for (var call = 0; call < record; call++)
            {
                using (StageTimings.Measure("links"))
                {
                    await Task.Yield();
                }
            }

            StageTimings.Count("links_written", record);
            summaries[record] = scope.Timings.Summary();
        });

        foreach (var (record, summary) in summaries)
        {
            Assert.Contains($"/{record} ", summary, StringComparison.Ordinal);
            Assert.EndsWith($"links_written={record}", summary, StringComparison.Ordinal);
        }
    }
}
