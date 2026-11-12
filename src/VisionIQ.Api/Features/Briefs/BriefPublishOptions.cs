using System.Globalization;

namespace VisionIQ.Api.Features.Briefs;

public enum BriefPublishMode
{
    /// <summary>Results are stored; nothing is rendered or posted (the default).</summary>
    Off,

    /// <summary>The comment is rendered and kept in <c>workflow.brief_publications</c>; no external call is made.</summary>
    Record,

    /// <summary>The comment is posted on the Azure DevOps work item.</summary>
    Ado,
}

/// <summary>
/// BRF-05 settings. Posting to Azure DevOps is the only place VisionIQ writes to a source system, so it is opt-in
/// (<c>BRIEF_PUBLISH_MODE=ado</c>) and uses its own token, <c>ADO_COMMENT_PAT</c>: a bot identity with
/// "Work Items (Read &amp; write)" and nothing else. The read token used everywhere else is never used to write.
/// </summary>
public sealed record BriefPublishOptions(
    BriefPublishMode Mode,
    Uri? OrganizationUrl,
    string? CommentToken,
    TimeSpan WorkerInterval)
{
    public static BriefPublishOptions Disabled { get; } = new(BriefPublishMode.Off, null, null, TimeSpan.FromSeconds(2));

    /// <exception cref="InvalidOperationException">A setting is invalid or the ado mode is incomplete.</exception>
    public static BriefPublishOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var mode = (configuration["BRIEF_PUBLISH_MODE"] ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" or "off" => BriefPublishMode.Off,
            "record" => BriefPublishMode.Record,
            "ado" => BriefPublishMode.Ado,
            _ => throw new InvalidOperationException("BRIEF_PUBLISH_MODE must be off, record or ado."),
        };
        var interval = 2.0;
        if (configuration["BRIEF_PUBLISH_INTERVAL_SECONDS"] is { Length: > 0 } text &&
            (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out interval) || interval is < 0.1 or > 60))
        {
            throw new InvalidOperationException("BRIEF_PUBLISH_INTERVAL_SECONDS must be a number from 0.1 to 60.");
        }

        if (mode != BriefPublishMode.Ado)
        {
            return new BriefPublishOptions(mode, null, null, TimeSpan.FromSeconds(interval));
        }

        var token = configuration["ADO_COMMENT_PAT"]?.Trim();
        if (!Uri.TryCreate(configuration["ADO_ORG_URL"], UriKind.Absolute, out var organization) ||
            organization.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("BRIEF_PUBLISH_MODE=ado needs ADO_ORG_URL (https) and ADO_COMMENT_PAT.");
        }

        if (string.Equals(token, configuration["ADO_READ_PAT"]?.Trim(), StringComparison.Ordinal))
        {
            // Keeps the read path provably read-only: its token must not be able to write.
            throw new InvalidOperationException("ADO_COMMENT_PAT must be a different token from ADO_READ_PAT.");
        }

        return new BriefPublishOptions(mode, organization, token, TimeSpan.FromSeconds(interval));
    }
}
