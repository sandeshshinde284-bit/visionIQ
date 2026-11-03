namespace VisionIQ.Api.Features.GraphApi;

/// <summary>
/// Configuration of the graph query API. The API is only exposed when every value is present; a partial
/// configuration is a startup error rather than a silently weaker setup.
/// </summary>
public sealed record GraphApiOptions(
    Guid TenantId,
    IReadOnlyList<string> Audiences,
    string RequiredScope,
    IReadOnlySet<string> AllowedCallerAppIds,
    string AdoInstance,
    IReadOnlyList<string> ApprovedProjects)
{
    /// <summary>Approved Confluence site and spaces (optional): candidate scopes; the live page check decides.</summary>
    public string? ConfluenceSite { get; init; }

    public IReadOnlyList<string> ConfluenceSpaces { get; init; } = [];

    public static readonly string[] Keys =
    [
        "VISIONIQ_AUTH_TENANT_ID", "VISIONIQ_AUTH_AUDIENCES", "VISIONIQ_AUTH_ALLOWED_CALLERS",
    ];

    /// <summary>Null when none of the graph API settings are present (the API stays unexposed).</summary>
    public static GraphApiOptions? FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var present = Keys.Where(key => !string.IsNullOrWhiteSpace(configuration[key])).ToList();
        if (present.Count == 0)
        {
            return null;
        }

        var problems = Keys.Except(present).Select(key => $"{key} is required").ToList();
        // A specific tenant only: "common"/"organizations" would accept tokens from any Entra tenant.
        if (!Guid.TryParse(configuration["VISIONIQ_AUTH_TENANT_ID"], out var tenant) || tenant == Guid.Empty)
        {
            problems.Add("VISIONIQ_AUTH_TENANT_ID must be the tenant's GUID (not 'common' or 'organizations')");
        }

        var audiences = Split(configuration["VISIONIQ_AUTH_AUDIENCES"]);
        var callers = Split(configuration["VISIONIQ_AUTH_ALLOWED_CALLERS"]);
        if (callers.Any(caller => !Guid.TryParse(caller, out _)))
        {
            problems.Add("VISIONIQ_AUTH_ALLOWED_CALLERS must be application (client) IDs");
        }

        var scope = configuration["VISIONIQ_AUTH_SCOPE"] is { Length: > 0 } configured ? configured.Trim() : "Graph.Read";
        var projects = Split(configuration["ADO_PROJECTS"]);
        if (!Uri.TryCreate(configuration["ADO_ORG_URL"], UriKind.Absolute, out var organization) || projects.Count == 0)
        {
            problems.Add("ADO_ORG_URL and ADO_PROJECTS are required: answers are filtered by ADO project membership");
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Graph API configuration is incomplete: " + string.Join("; ", problems) + ".");
        }

        var spaces = Split(configuration["CONFLUENCE_SPACE_KEYS"]);
        var site = Uri.TryCreate(configuration["CONFLUENCE_BASE_URL"], UriKind.Absolute, out var confluence) && spaces.Count > 0
            ? confluence.AbsoluteUri.TrimEnd('/')
            : null;
        return new GraphApiOptions(tenant, audiences, scope,
            new HashSet<string>(callers, StringComparer.OrdinalIgnoreCase),
            organization!.AbsoluteUri.TrimEnd('/'), projects)
        {
            ConfluenceSite = site,
            ConfluenceSpaces = site is null ? [] : spaces,
        };
    }

    private static List<string> Split(string? value) =>
        (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
