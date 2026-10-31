using System.Globalization;
using VisionIQ.Api.Features.Backfill;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Cli;

/// <summary>
/// Reads one authenticated Azure DevOps work-item revision after a source event.
/// The runtime uses the existing read-only ADO client and never trusts webhook bodies.
/// </summary>
public interface IAdoChangedWorkItemReader
{
    /// <summary>Fetches one specified approved work item, or returns a partial source outcome.</summary>
    Task<ReadOutcome> ReadAsync(
        string project, int id, CancellationToken cancellationToken = default);
}

/// <summary>Production single-record adapter around the existing batched ADO API.</summary>
public sealed class AdoChangedWorkItemReader(AdoWorkItemSource source) : IAdoChangedWorkItemReader
{
    /// <summary>Requires exactly one returned work item and never hides omitted IDs.</summary>
    public async Task<ReadOutcome> ReadAsync(
        string project, int id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        if (id < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        var records = await source.GetWorkItemsAsync(project, [id], cancellationToken)
            .ConfigureAwait(false);
        return records.Count == 1
            ? records[0]
            : new ReadOutcome(null, "Provider did not return exactly one requested work item.");
    }
}

/// <summary>
/// LRN-05 concrete internal handler for authenticated ADO work-item change notifications.
/// Events for unknown projects, kinds or source revisions are rejected rather than
/// silently acknowledged; transport verification remains the FND-06 responsibility.
/// </summary>
public sealed class AdoWorkItemChangeHandler : ISourceChangeHandler
{
    private readonly string tenant;
    private readonly HashSet<string> approvedProjects;
    private readonly IAdoChangedWorkItemReader reader;
    private readonly ICanonicalEvidenceSink evidence;

    /// <summary>Configures a bounded allowlist from the existing ADO connection settings.</summary>
    public AdoWorkItemChangeHandler(
        AdoOptions options, IAdoChangedWorkItemReader reader,
        ICanonicalEvidenceSink evidence)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(evidence);
        tenant = options.Tenant;
        approvedProjects = new HashSet<string>(options.Projects, StringComparer.Ordinal);
        this.reader = reader;
        this.evidence = evidence;
    }

    /// <summary>
    /// Re-fetches the specified work item under configured read-only ADO credentials,
    /// verifies its native identity/revision and archives before acknowledging an event.
    /// </summary>
    public async Task ApplyAsync(
        SourceChangeNotification change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        change.Validate();
        if (!string.Equals(change.ProviderKind, "work_item", StringComparison.Ordinal))
        {
            throw new NotSupportedException("Unsupported event kind; do not acknowledge it.");
        }

        var sourcePrefix = tenant + "/ado/";
        if (!change.SourceId.StartsWith(sourcePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Notification does not belong to the approved ADO tenant.");
        }

        var project = change.SourceId[sourcePrefix.Length..];
        if (!approvedProjects.Contains(project))
        {
            throw new InvalidOperationException("Notification project is not approved for ingestion.");
        }

        if (!int.TryParse(change.NativeId, NumberStyles.None, CultureInfo.InvariantCulture,
                out var id) || id <= 0 ||
            !int.TryParse(change.SourceRevision, NumberStyles.None, CultureInfo.InvariantCulture,
                out var revision) || revision <= 0)
        {
            throw new ArgumentException("Notification requires valid numeric work-item ID and revision.");
        }

        var outcome = await reader.ReadAsync(project, id, cancellationToken).ConfigureAwait(false);
        if (outcome.Record is not { } record ||
            record.Id != id || record.Revision < revision)
        {
            throw new InvalidOperationException(
                "The requested source item/revision is missing or inaccessible.");
        }

        var sourced = record with { Project = project };
        var key = tenant + "/ado/" + project + "/work-item/" +
            id.ToString(CultureInfo.InvariantCulture) + "@" +
            sourced.Revision.ToString(CultureInfo.InvariantCulture);
        await evidence.WriteAsync("work-items", key, sourced, cancellationToken)
            .ConfigureAwait(false);
    }
}
