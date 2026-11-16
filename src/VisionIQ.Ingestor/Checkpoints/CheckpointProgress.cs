namespace VisionIQ.Ingestor.Checkpoints;

/// <summary>
/// How far a checkpoint may move after a batch that was processed in parallel. Items complete in any order, so
/// the checkpoint may only cover the longest run of items, in the batch's own (timestamp, ID) order, that are all
/// safe to leave behind. It stops at the first item that is not: everything from there on is read again next run.
/// </summary>
public static class CheckpointProgress
{
    /// <summary>
    /// Positions of the ordered items up to (not including) the first one for which <paramref name="safe"/> is
    /// false. <paramref name="ordered"/> must be in the same order the source is read in (timestamp, then ID).
    /// </summary>
    public static IReadOnlyList<(DateTimeOffset At, string Id)> SafePrefix<T>(
        IReadOnlyList<T> ordered,
        Func<T, (DateTimeOffset At, string Id)> position,
        Func<T, bool> safe)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(safe);

        var prefix = new List<(DateTimeOffset At, string Id)>();
        foreach (var item in ordered)
        {
            if (!safe(item))
            {
                break;
            }

            prefix.Add(position(item));
        }

        return prefix;
    }
}
