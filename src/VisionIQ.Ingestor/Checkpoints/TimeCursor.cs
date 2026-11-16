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

    /// <summary>
    /// The timestamp part of a saved cursor (everything before the first '|'). <see cref="Format"/> writes it as
    /// fixed-width ISO-8601 UTC, so ordinal comparison of this part is chronological comparison.
    /// </summary>
    public static string TimestampPart(string formatted)
    {
        ArgumentNullException.ThrowIfNull(formatted);
        var separator = formatted.IndexOf('|', StringComparison.Ordinal);
        return separator >= 0 ? formatted[..separator] : formatted;
    }

    /// <summary>
    /// True when saving <paramref name="candidate"/> over <paramref name="existing"/> would move the checkpoint back
    /// in time. Only the timestamp is compared: at an equal timestamp the boundary-ID list is a set, not an order
    /// ("T|10,9" is the set {9, 10} that grew from "T|9"), and comparing it as text wrongly rejected that growth.
    /// A smaller ID set at an equal timestamp only causes those items to be read again, never skipped.
    /// </summary>
    public static bool IsBehind(string candidate, string existing) =>
        string.CompareOrdinal(TimestampPart(candidate), TimestampPart(existing)) < 0;
}
