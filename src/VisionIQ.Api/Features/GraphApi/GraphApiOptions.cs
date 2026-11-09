namespace VisionIQ.Api.Features.GraphApi;

public sealed record GraphApiOptions(
    Guid TenantId,
    IReadOnlyList<string> Audiences,
    string RequiredScope,
    IReadOnlySet<string> AllowedCallerAppIds,
    string AdoInstance,
    IReadOnlyList<string> ApprovedProjects)
{
    public string? ConfluenceSite { get; init; }
    public IReadOnlyList<string> ConfluenceSpaces { get; init; } = [];

    public static GraphApiOptions? FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return null;
    }

    private static List<string> Split(string? value) =>
        (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
