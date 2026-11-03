using System.Globalization;
using VisionIQ.Api.Features.Evidence;
using VisionIQ.Api.Features.TestLinks;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Links;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Cli;

/// <summary>
/// LRN-07: after a current work item is stored as evidence, records its current declared test links (ADO
/// "Tested By") and, for a Test Case, its automated test identity. Evidence is written first, so every
/// projected row cites a stored revision.
/// </summary>
public sealed class TestLinkProjectionSink(
    ICanonicalEvidenceSink evidence,
    ITestLinkStore testLinks,
    Uri adoOrganization) : ICanonicalEvidenceSink
{
    private readonly string sourceInstance = adoOrganization.AbsoluteUri.TrimEnd('/');

    public async Task WriteAsync(string stream, string key, object record, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        ArgumentNullException.ThrowIfNull(record);

        await evidence.WriteAsync(stream, key, record, cancellationToken).ConfigureAwait(false);
        if (stream == "work-items" && record is WorkItemRecord item)
        {
            await testLinks.ReplaceAsync(Snapshot(sourceInstance, item), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The work item's current test links, citing the exact current-work-item evidence revision.</summary>
    public static TestLinkSnapshot Snapshot(string sourceInstance, WorkItemRecord item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var id = item.Id.ToString(CultureInfo.InvariantCulture);
        var itemKey = new EvidenceIdentity("azure_devops", sourceInstance, item.Project, null, "work_item_current", id).ItemKey();
        var reference = "evidence:" + itemKey + ":" + CanonicalEvidenceSink.ContentRevision(item.Revision.ToString(CultureInfo.InvariantCulture), item);
        var testCases = LinkExtractor.FromWorkItem(item)
            .Where(link => link.LinkType == "tested_by" && link.ToKind == EntityKind.WorkItem)
            .Select(link => link.ToId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var name = string.IsNullOrWhiteSpace(item.AutomatedTestName) ? null : item.AutomatedTestName.Trim();
        return new TestLinkSnapshot(sourceInstance, item.Project, id, testCases, name, item.AutomatedTestStorage, reference, item.ChangedDate);
    }
}
