using System.Globalization;

namespace VisionIQ.Ingestor.Checkpoints;

/// <summary>
/// A resume point: the newest timestamp processed, plus every item ID processed at exactly that time.
/// Items at the same timestamp are skipped by ID, so a resumed run never repeats them.
/// </summary>
public sealed record TimeCursor(DateTimeOffset At, IReadOnlySet<string> Boundary)
{
    public static TimeCursor? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var separator = text.IndexOf('|', StringComparison.Ordinal);
        var timePart = separator >= 0 ? text[..separator] : text;
        if (!DateTimeOffset.TryParse(timePart, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
        {
            throw new FormatException("The saved checkpoint is not readable. Remove it to read the source again from INGESTION_SINCE.");
        }

        var ids = separator >= 0
            ? text[(separator + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        return new TimeCursor(at.ToUniversalTime(), new HashSet<string>(ids, StringComparer.Ordinal));
    }

    public static TimeCursor? Advance(TimeCursor? current, IEnumerable<(DateTimeOffset At, string Id)> processed)
    {
        ArgumentNullException.ThrowIfNull(processed);
        var items = processed.ToList();
        if (items.Count == 0)
        {
            return current;
        }

        var newest = items.Max(item => item.At);
        if (current is not null && current.At > newest)
        {
            return current;
        }

        var ids = new HashSet<string>(items.Where(item => item.At == newest).Select(item => item.Id), StringComparer.Ordinal);
        if (current is not null && current.At == newest)
        {
            ids.UnionWith(current.Boundary);
        }

        return new TimeCursor(newest.ToUniversalTime(), ids);
    }

    /// <summary>True when an item at this time and ID has not been processed yet.</summary>
    public bool IsNew(DateTimeOffset at, string id) => at > At || (at == At && !Boundary.Contains(id));

    public string Format() =>
        At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) + "|" + string.Join(',', Boundary.Order(StringComparer.Ordinal));
}
