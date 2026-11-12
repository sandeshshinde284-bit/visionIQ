using System.Text.Json;
using Npgsql;
using VisionIQ.Api.Features.Pickup;

namespace VisionIQ.Api.Features.Fixes;

/// <summary>A pull request whose source branch moved: where it is and the commit now at its head.</summary>
public sealed record PullRequestUpdate(string RepositoryId, string RepositoryName, string PullRequestId, string HeadSha);

/// <summary>
/// Reads the ADO "Pull request updated" service-hook body (<c>git.pullrequest.updated</c>). Only the repository,
/// the pull request id and the head commit are read; titles, descriptions and people are not kept.
/// </summary>
public static class AdoPullRequestHook
{
    public const string PullRequestUpdated = "git.pullrequest.updated";

    /// <summary>The update a delivery reports; null when the body is another event or cannot be used.</summary>
    public static PullRequestUpdate? Parse(ReadOnlySpan<byte> body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "eventType") != PullRequestUpdated ||
                !root.TryGetProperty("resource", out var resource) || resource.ValueKind != JsonValueKind.Object ||
                !resource.TryGetProperty("pullRequestId", out var id) || id.ValueKind != JsonValueKind.Number ||
                !resource.TryGetProperty("repository", out var repository) ||
                Text(repository, "id") is not { } repositoryId ||
                !resource.TryGetProperty("lastMergeSourceCommit", out var source) ||
                Text(source, "commitId")?.ToLowerInvariant() is not { } head || !FixPatterns.Sha().IsMatch(head))
            {
                // Anything that is not a full commit id would be refused by the database later, after the
                // statuses were already posted; it is refused here instead.
                return null;
            }

            return new PullRequestUpdate(repositoryId, Text(repository, "name") ?? repositoryId, id.GetRawText(), head);
        }
    }

    /// <summary>Handles the delivery on the shared ADO webhook; 503 lets ADO retry it.</summary>
    public static async Task<IResult> HandleAsync(
        PullRequestUpdate update, IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService<FixPullRequestHeadWatcher>() is not { } watcher)
        {
            return Results.Accepted(value: new PickupResponse("ignored", "fix_pull_requests_off"));
        }

        try
        {
            var outcome = await watcher.OnUpdatedAsync(update, cancellationToken);
            var recorded = outcome is FixPullRequestHeadWatcher.Invalidated or FixPullRequestHeadWatcher.Restored;
            return Results.Accepted(value: new PickupResponse(recorded ? "recorded" : "ignored", outcome));
        }
        catch (FixPublishException error) when (error.Permanent)
        {
            // No permission, or the repository is gone: the same delivery would fail every time, and a
            // subscription that only ever fails is disabled by Azure DevOps. It is accepted and reported.
            FixLog.HeadUpdateRejected(logger, error.Reason);
            return Results.Accepted(value: new PickupResponse("ignored", error.Reason));
        }
        catch (Exception error) when (error is FixPublishException or NpgsqlException or TimeoutException)
        {
            // Type only: provider messages can include connection details.
            FixLog.HeadUpdateFailed(logger, error.GetType().Name);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
