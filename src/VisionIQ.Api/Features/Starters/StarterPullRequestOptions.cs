namespace VisionIQ.Api.Features.Starters;

public enum StarterPullRequestMode
{
    /// <summary>No starter pull request is ever asked for (the default). Guidance alone completes the path.</summary>
    Off,

    /// <summary>Only when an engineer asks for one with a comment on the ticket.</summary>
    OnRequest,

    /// <summary>On request, and also without being asked whenever the guidance came out PUBLISHED.</summary>
    Auto,
}

/// <summary>
/// ENH-02 (PROP-04): when a starter pull request is asked for. "A starter PR is created only when the engineer
/// asks for it or policy allows it": <c>STARTER_PR_MODE=on_request</c> is the engineer asking, <c>auto</c> is the
/// policy. Either way Intelligence still decides whether the change is understood well enough, and the
/// repository's owners must have switched starters on in the fix policy. The pull request itself is opened by
/// the DEF-05 delivery (<c>FIX_PR_MODE</c>, <c>ADO_PR_PAT</c>): there is one bot identity for draft pull requests.
/// </summary>
public sealed record StarterPullRequestOptions(StarterPullRequestMode Mode)
{
    public static StarterPullRequestOptions Disabled { get; } = new(StarterPullRequestMode.Off);

    public bool Enabled => Mode != StarterPullRequestMode.Off;

    /// <exception cref="InvalidOperationException">The setting is not one of the three values.</exception>
    public static StarterPullRequestOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new StarterPullRequestOptions((configuration["STARTER_PR_MODE"] ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" or "off" => StarterPullRequestMode.Off,
            "on_request" => StarterPullRequestMode.OnRequest,
            "auto" => StarterPullRequestMode.Auto,
            _ => throw new InvalidOperationException("STARTER_PR_MODE must be off, on_request or auto."),
        });
    }
}
