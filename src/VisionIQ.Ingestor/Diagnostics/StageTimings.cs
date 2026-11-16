using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VisionIQ.Ingestor.Diagnostics;

/// <summary>
/// Per-record timing of the ingestion stages (ADO enrichment, Cloud Storage archive, PostgreSQL evidence write,
/// ticket-fix projection, reviews, links), so one log line per record shows where its time went.
/// <para>
/// A runner opens a scope per record with <see cref="Begin"/>; any code running inside that record's async flow
/// (the sinks, the evidence service, the link stores) adds to it with <see cref="Measure"/> or <see cref="Count"/>.
/// The scope travels with the async flow (AsyncLocal), so parallel records never mix their timings. Outside a
/// scope every call is a no-op, so nothing changes for callers that do not measure.
/// </para>
/// </summary>
public sealed class StageTimings
{
    private static readonly AsyncLocal<StageTimings?> Current = new();

    private readonly ConcurrentDictionary<string, (long Ticks, int Calls)> stages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> counts = new(StringComparer.Ordinal);

    private StageTimings()
    {
    }

    /// <summary>Starts timing one record; dispose the returned scope when the record is done.</summary>
    public static Scope Begin()
    {
        var previous = Current.Value;
        var timings = new StageTimings();
        Current.Value = timings;
        return new Scope(timings, previous);
    }

    /// <summary>Times one call of <paramref name="stage"/> in the current record (no-op outside a scope).</summary>
    public static Measurement Measure(string stage) => new(Current.Value, stage);

    /// <summary>Adds to a counter of the current record, e.g. claims written (no-op outside a scope).</summary>
    public static void Count(string name, long value)
    {
        if (Current.Value is { } timings && value != 0)
        {
            timings.counts.AddOrUpdate(name, value, (_, total) => total + value);
        }
    }

    /// <summary>Total milliseconds spent in <paramref name="stage"/> so far (0 when never measured).</summary>
    public double Milliseconds(string stage) =>
        stages.TryGetValue(stage, out var entry) ? Stopwatch.GetElapsedTime(0, entry.Ticks).TotalMilliseconds : 0;

    /// <summary>"stage=12ms/3 ..., counter=5" in a fixed (ordinal) order, for one log line.</summary>
    public string Summary()
    {
        var text = new StringBuilder();
        foreach (var (stage, entry) in stages.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture,
                $"{stage}={Stopwatch.GetElapsedTime(0, entry.Ticks).TotalMilliseconds:0}ms/{entry.Calls} ");
        }

        foreach (var (name, value) in counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"{name}={value} ");
        }

        return text.ToString().TrimEnd();
    }

    private void Add(string stage, long ticks) =>
        stages.AddOrUpdate(stage, (ticks, 1), (_, entry) => (entry.Ticks + ticks, entry.Calls + 1));

    /// <summary>The timing scope of one record; restores the enclosing scope (if any) when disposed.</summary>
    public readonly struct Scope : IDisposable, IEquatable<Scope>
    {
        private readonly StageTimings? previous;

        internal Scope(StageTimings timings, StageTimings? previous)
        {
            Timings = timings;
            this.previous = previous;
        }

        public StageTimings Timings { get; }

        public void Dispose() => Current.Value = previous;

        public bool Equals(Scope other) => ReferenceEquals(Timings, other.Timings);

        public override bool Equals(object? obj) => obj is Scope other && Equals(other);

        public override int GetHashCode() => Timings?.GetHashCode() ?? 0;

        public static bool operator ==(Scope left, Scope right) => left.Equals(right);

        public static bool operator !=(Scope left, Scope right) => !left.Equals(right);
    }

    /// <summary>One timed call; adds its elapsed time to the record when disposed.</summary>
    public readonly struct Measurement : IDisposable, IEquatable<Measurement>
    {
        private readonly StageTimings? timings;
        private readonly string stage;
        private readonly long started;

        internal Measurement(StageTimings? timings, string stage)
        {
            this.timings = timings;
            this.stage = stage;
            started = timings is null ? 0 : Stopwatch.GetTimestamp();
        }

        public void Dispose() => timings?.Add(stage, Stopwatch.GetTimestamp() - started);

        public bool Equals(Measurement other) =>
            ReferenceEquals(timings, other.timings) && stage == other.stage && started == other.started;

        public override bool Equals(object? obj) => obj is Measurement other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(timings, stage, started);

        public static bool operator ==(Measurement left, Measurement right) => left.Equals(right);

        public static bool operator !=(Measurement left, Measurement right) => !left.Equals(right);
    }
}
