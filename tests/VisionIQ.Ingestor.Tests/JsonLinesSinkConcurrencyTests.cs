using System.Text.Json;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Tests;

/// <summary>
/// R2-01: SyncRunner now writes several work items' evidence concurrently in jsonl mode too, so this
/// sink's per-stream dictionaries and StreamWriter must tolerate concurrent WriteAsync calls instead of
/// corrupting the file or throwing. These tests exercise that directly, independent of SyncRunner.
/// </summary>
public sealed class JsonLinesSinkConcurrencyTests
{
    [Fact]
    public async Task Concurrent_writes_across_streams_never_throw_and_every_record_survives()
    {
        var dir = Directory.CreateTempSubdirectory("visioniq-jsonl-concurrency-").FullName;
        try
        {
            const int perStream = 100;
            var streams = new[] { "work-items", "work-item-revisions", "links" };

            using (var sink = new JsonLinesSink(dir))
            {
                var tasks = streams.SelectMany(stream => Enumerable.Range(0, perStream)
                    .Select(i => sink.WriteAsync(stream, stream + "-key-" + i, new { stream, i })));

                await Task.WhenAll(tasks);
                await sink.FlushAsync();
            } // dispose closes every StreamWriter/FileStream before the files are read back below

            foreach (var stream in streams)
            {
                var lines = await File.ReadAllLinesAsync(Path.Combine(dir, stream + ".jsonl"));
                Assert.Equal(perStream, lines.Length);

                var keys = lines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("key").GetString())
                    .ToList();
                Assert.Equal(perStream, keys.Distinct(StringComparer.Ordinal).Count());
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Concurrent_writes_of_the_same_key_are_deduplicated_not_duplicated_or_corrupted()
    {
        var dir = Directory.CreateTempSubdirectory("visioniq-jsonl-concurrency-").FullName;
        try
        {
            using (var sink = new JsonLinesSink(dir))
            {
                var tasks = Enumerable.Range(0, 50).Select(i => sink.WriteAsync("work-items", "same-key", new { i }));
                await Task.WhenAll(tasks);
                await sink.FlushAsync();
            } // dispose closes the StreamWriter/FileStream before the file is read back below

            var lines = await File.ReadAllLinesAsync(Path.Combine(dir, "work-items.jsonl"));
            Assert.Single(lines);
            JsonDocument.Parse(lines[0]); // still valid, uncorrupted JSON
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
