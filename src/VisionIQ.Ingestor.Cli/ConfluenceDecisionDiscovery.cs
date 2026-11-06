using System.Text.RegularExpressions;
using VisionIQ.Ingestor.Configuration;
using VisionIQ.Ingestor.Confluence;

namespace VisionIQ.Ingestor.Cli;

/// <summary>
/// Read-only discovery of title- and label-based decision candidates. Does not touch sync sinks, checkpoints,
/// canonical storage or body endpoints. Never reports page titles, IDs, links or user/group memberships.
/// </summary>
internal static class ConfluenceDecisionDiscovery
{
    private static readonly Regex DecisionTitle = new(
        @"^\s*(ADR[-\s]?\d+|Decision\b|Design decision\b|Architecture decision\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly HashSet<string> DecisionLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "decision", "decisions", "decision-log", "adr", "design-decision", "architecture-decision",
    };

    public static async Task<int> RunAsync()
    {
        IngestorSettings settings;
        try
        {
            settings = IngestorSettings.FromValues(EnvFile.LoadMerged(Directory.GetCurrentDirectory()));
        }
        catch (InvalidOperationException error)
        {
            Console.Error.WriteLine("Configuration invalid: " + error.Message);
            return 1;
        }

        if (settings.Confluence is not { Deployment: ConfluenceDeployment.Cloud } cloud)
        {
            Console.Error.WriteLine("Metadata-only discovery currently supports configured Confluence Cloud spaces only.");
            return 1;
        }

        using var http = new HttpClient { Timeout = settings.RequestTimeout };
        var source = new ConfluenceCloudSource(new ConfluenceHttp(http, cloud));
        var anyFailed = false;
        foreach (var space in cloud.SpaceKeys)
        {
            try
            {
                var pages = await source.GetPageMetadataAsync(space);
                var acl = ConfluenceAccess.ComputeAcls(pages);
                var eligible = pages.Where(p => acl[p.Id].Complete && !acl[p.Id].IsRestricted).ToList();
                var titleCandidates = eligible.Count(p => DecisionTitle.IsMatch(p.Title));
                var labelCandidates = 0;
                var labelFailures = 0;
                foreach (var page in eligible)
                {
                    // Never retrieve labels for restricted or incomplete-ACL pages.
                    try
                    {
                        var labels = await source.GetPageLabelsAsync(page.Id);
                        if (labels.Any(DecisionLabels.Contains))
                        {
                            labelCandidates++;
                        }
                    }
                    catch (ConfluenceSourceException)
                    {
                        // One page's label lookup failing (e.g. transient 5xx) shouldn't abort the
                        // whole space scan; it's counted and surfaced as a non-zero exit instead.
                        labelFailures++;
                    }
                }
                Console.WriteLine($"Confluence space {space}: pages={pages.Count}, eligible-complete-unrestricted={eligible.Count}, title-candidates={titleCandidates}, label-candidates={labelCandidates}, label-lookup-failures={labelFailures}, withheld-or-unverified={pages.Count - eligible.Count}");
                Console.WriteLine("Title/label counts can overlap. Template properties not inspected; zero candidates does not prove no design decisions.");
                Console.WriteLine("No titles, IDs, bodies, ACL members, checkpoint writes, or evidence exports.");
                if (labelFailures > 0)
                {
                    anyFailed = true;
                }
            }
            catch (ConfluenceSourceException error)
            {
                anyFailed = true;
                Console.Error.WriteLine($"Confluence space {space}: {(error.IsAccessDenied ? "access denied" : "unreachable")}; no candidates disclosed.");
            }
        }

        return anyFailed ? 2 : 0;
    }
}
