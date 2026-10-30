using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionIQ.Ingestor.Sync;

/// <summary>Where synced records go. The canonical database store comes with LRN-03.</summary>
public interface IRecordSink
{
    Task WriteAsync(string stream, string key, object record, CancellationToken cancellationToken = default);
}

/// <summary>Writes one JSON object per line per stream (work-items.jsonl, links.jsonl, ...), skipping repeated keys.</summary>
public sealed class JsonLinesSink : IRecordSink, IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string directory;
    private readonly TimeProvider time;
    private readonly Dictionary<string, StreamWriter> writers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> seen = new(StringComparer.Ordinal);

    public JsonLinesSink(string directory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = directory;
        this.time = time ?? TimeProvider.System;
        Directory.CreateDirectory(directory);
    }

    public async Task WriteAsync(string stream, string key, object record, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(record);

        if (!seen.TryGetValue(stream, out var keys))
        {
            keys = new HashSet<string>(StringComparer.Ordinal);
            seen[stream] = keys;
        }

        if (!keys.Add(key))
        {
            return;
        }

        if (!writers.TryGetValue(stream, out var writer))
        {
            writer = new StreamWriter(Path.Combine(directory, stream + ".jsonl"), append: true);
            writers[stream] = writer;
        }

        var line = JsonSerializer.Serialize(new { key, fetchedAt = time.GetUtcNow(), record }, Options);
        await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        foreach (var writer in writers.Values)
        {
            writer.Dispose();
        }

        writers.Clear();
    }
}
