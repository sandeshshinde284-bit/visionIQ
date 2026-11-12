using System.Globalization;

namespace VisionIQ.Api.Features.Fixes;

public enum FixPullRequestMode
{
    /// <summary>Fix results are stored; no pull request is prepared (the default).</summary>
    Off,

    /// <summary>What would be pushed and opened is kept in <c>workflow.fix_pull_requests</c>; no external call.</summary>
    Record,

    /// <summary>The branch is pushed and a draft pull request is opened in Azure DevOps.</summary>
    Ado,
}

/// <summary>
/// DEF-05 settings. Opening a pull request writes to a source system, so it is opt-in (<c>FIX_PR_MODE=ado</c>)
/// and uses its own token, <c>ADO_PR_PAT</c>: a bot identity that may read code, create branches and create pull
/// requests, and nothing else (HG-01: it must not be able to approve or complete a pull request). The read token
/// and the comment token are never used for this.
/// </summary>
public sealed record FixPullRequestOptions(
    FixPullRequestMode Mode,
    Uri? OrganizationUrl,
    string? PullRequestToken,
    TimeSpan WorkerInterval,
    bool AutoStart)
{
    public static FixPullRequestOptions Disabled { get; } =
        new(FixPullRequestMode.Off, null, null, TimeSpan.FromSeconds(2), false);

    /// <exception cref="InvalidOperationException">A setting is invalid or the ado mode is incomplete.</exception>
    public static FixPullRequestOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var mode = (configuration["FIX_PR_MODE"] ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" or "off" => FixPullRequestMode.Off,
            "record" => FixPullRequestMode.Record,
            "ado" => FixPullRequestMode.Ado,
            _ => throw new InvalidOperationException("FIX_PR_MODE must be off, record or ado."),
        };
        var interval = 2.0;
        if (configuration["FIX_PR_INTERVAL_SECONDS"] is { Length: > 0 } text &&
            (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out interval) || interval is < 0.1 or > 60))
        {
            throw new InvalidOperationException("FIX_PR_INTERVAL_SECONDS must be a number from 0.1 to 60.");
        }

        // FIX_AUTO_START: after a defect ticket's brief, ask Intelligence for a fix run (analysis mode "deep").
        var autoStart = (configuration["FIX_AUTO_START"] ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" or "false" or "0" => false,
            "true" or "1" => true,
            _ => throw new InvalidOperationException("FIX_AUTO_START must be true or false."),
        };

        if (mode != FixPullRequestMode.Ado)
        {
            return new FixPullRequestOptions(mode, null, null, TimeSpan.FromSeconds(interval), autoStart);
        }

        var token = configuration["ADO_PR_PAT"]?.Trim();
        if (!Uri.TryCreate(configuration["ADO_ORG_URL"], UriKind.Absolute, out var organization) ||
            organization.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("FIX_PR_MODE=ado needs ADO_ORG_URL (https) and ADO_PR_PAT.");
        }

        if (string.Equals(token, configuration["ADO_READ_PAT"]?.Trim(), StringComparison.Ordinal) ||
            string.Equals(token, configuration["ADO_COMMENT_PAT"]?.Trim(), StringComparison.Ordinal))
        {
            // One token per kind of write, so each can be scoped to exactly what it does.
            throw new InvalidOperationException("ADO_PR_PAT must be a different token from ADO_READ_PAT and ADO_COMMENT_PAT.");
        }

        return new FixPullRequestOptions(mode, organization, token, TimeSpan.FromSeconds(interval), autoStart);
    }
}
