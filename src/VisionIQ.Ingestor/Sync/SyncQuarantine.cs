namespace VisionIQ.Ingestor.Sync;

/// <summary>G-1 (LRN-05): one source item that could not be read in a sync run.</summary>
public sealed record QuarantinedItem(
    string SourceId, string Scope, string ItemKind, string NativeId, string ReasonCode, string Reason, DateTimeOffset ObservedAt);

/// <summary>
/// The per-item quarantine list for scheduled syncs. A failed item is listed (with attempts and reason) instead
/// of stopping the run; it is still retried because the stream checkpoint does not advance past it, and its
/// entry is marked resolved once it is read successfully.
/// </summary>
public interface ISyncQuarantine
{
    /// <summary>Native IDs currently quarantined (unresolved) for this source, scope and item kind.</summary>
    Task<IReadOnlySet<string>> OpenAsync(string sourceId, string scope, string itemKind, CancellationToken cancellationToken = default);

    Task AddAsync(QuarantinedItem item, CancellationToken cancellationToken = default);

    Task ResolveAsync(string sourceId, string scope, string itemKind, string nativeId, DateTimeOffset resolvedAt, CancellationToken cancellationToken = default);
}

/// <summary>Process-local quarantine (local JSONL mode and tests).</summary>
public sealed class InMemorySyncQuarantine : ISyncQuarantine
{
    private readonly Dictionary<(string, string, string, string), (QuarantinedItem Item, int Attempts, DateTimeOffset? Resolved)> items = [];

    public IReadOnlyList<(QuarantinedItem Item, int Attempts, DateTimeOffset? Resolved)> Entries => items.Values.ToList();

    public Task<IReadOnlySet<string>> OpenAsync(string sourceId, string scope, string itemKind, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlySet<string>>(items
            .Where(pair => pair.Key.Item1 == sourceId && pair.Key.Item2 == scope && pair.Key.Item3 == itemKind && pair.Value.Resolved is null)
            .Select(pair => pair.Key.Item4)
            .ToHashSet(StringComparer.Ordinal));

    public Task AddAsync(QuarantinedItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var key = (item.SourceId, item.Scope, item.ItemKind, item.NativeId);
        // Only chain the attempt count while the previous entry is still open. A failure that shows up
        // after the item was resolved is a fresh problem, not a continuation of the old one.
        var attempts = items.TryGetValue(key, out var existing) && existing.Resolved is null ? existing.Attempts + 1 : 1;
        items[key] = (item, attempts, null);
        return Task.CompletedTask;
    }

    public Task ResolveAsync(string sourceId, string scope, string itemKind, string nativeId, DateTimeOffset resolvedAt, CancellationToken cancellationToken = default)
    {
        var key = (sourceId, scope, itemKind, nativeId);
        // Guard against resolving twice: a later run finding the item readable again should reopen it via
        // AddAsync, not silently push the resolved timestamp forward.
        if (items.TryGetValue(key, out var existing) && existing.Resolved is null)
        {
            items[key] = (existing.Item, existing.Attempts, resolvedAt);
        }

        return Task.CompletedTask;
    }
}
