using System.Text.RegularExpressions;
using VisionIQ.Ingestor.Configuration;
using VisionIQ.Ingestor.Confluence;

namespace VisionIQ.Ingestor.Cli;

/// <summary>
/// Read-only discovery of title-based decision candidates. Does not touch sync sinks, checkpoints,
/// canonical storage or body endpoints. Never reports page titles, IDs, links or user/group memberships.
/// </summary>
internal static class ConfluenceDecisionDiscovery
{
    private static readonly Regex DecisionTitle = new(
        @"^\s*(ADR[-\s]?\d+|Decision\b|Design decision\b|Architecture decision\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

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
                var candidates = eligible.Count(p => DecisionTitle.IsMatch(p.Title));
                Console.WriteLine($"Confluence space {space}: pages={pages.Count}, eligible-complete-unrestricted={eligible.Count}, title-candidates={candidates}, withheld-or-unverified={pages.Count - eligible.Count}");
                Console.WriteLine("Metadata-only title scan. Labels and decision-template properties not inspected; zero candidates is not proof of no decisions.");
                Console.WriteLine("No titles, IDs, bodies, ACL members, checkpoint writes, or evidence exports.");
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
