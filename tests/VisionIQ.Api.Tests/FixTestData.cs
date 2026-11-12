using System.Net;
using System.Text;
using VisionIQ.Api.Features.Fixes;

namespace VisionIQ.Api.Tests;

/// <summary>
/// Fix results as Intelligence stores them. The fixtures in contracts/fixes/fixtures were written by real runs of
/// the Python pipeline; a Python test checks them against the schema, so both services read the same documents.
/// </summary>
internal static class FixTestData
{
    public static readonly Guid RunId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
    public static readonly Guid ResultId = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");
    public const string PushedSha = "1111111111111111111111111111111111111111";
    public const string NewHeadSha = "2222222222222222222222222222222222222222";

    public static string VerifiedJson { get; } = Fixture("fix-result.verified.v1.json");

    public static string BlockedJson { get; } = Fixture("fix-result.blocked.v1.json");

    public static FixDocument Verified { get; } = FixDocument.Parse(VerifiedJson)!;

    public static PendingFixPullRequest Pending(string? json, int attempt = 1) =>
        new(RunId, ResultId, "payments-core", "4711", attempt, json);

    public static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(EventContractTests.ContractsDirectory, "..", "fixes", "fixtures", name));

    internal sealed record Opened(string Project, FixDocument Fix, bool MayAlreadyExist);

    internal sealed record PostedChecks(string Project, string RepositoryId, string PullRequestId, IReadOnlyList<FixCheck> Checks);

    internal sealed class FakePullRequests(FixPullRequestMode mode) : IFixPullRequestPublisher
    {
        public List<Opened> Published { get; } = [];

        public List<PostedChecks> Checks { get; } = [];

        public Exception? Failure { get; init; }

        public Exception? CheckFailure { get; init; }

        /// <summary>The head the platform reports; null leaves the watcher with the delivery's own head.</summary>
        public string? CurrentHead { get; init; }

        public FixPullRequestMode Mode => mode;

        public Task<string?> CurrentHeadAsync(
            string project, string repositoryId, string pullRequestId, CancellationToken cancellationToken) =>
            Task.FromResult(CurrentHead);

        public Task<PublishedPullRequest> PublishAsync(
            string project, FixDocument fix, bool mayAlreadyExist, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Published.Add(new Opened(project, fix, mayAlreadyExist));
            return Task.FromResult(new PublishedPullRequest("812", PushedSha));
        }

        public Task PostChecksAsync(
            string project, string repositoryId, string pullRequestId, IReadOnlyList<FixCheck> checks,
            CancellationToken cancellationToken)
        {
            if (CheckFailure is not null)
            {
                throw CheckFailure;
            }

            Checks.Add(new PostedChecks(project, repositoryId, pullRequestId, checks));
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeFixStore(params PendingFixPullRequest[] pending) : IFixPullRequestStore
    {
        private readonly Queue<PendingFixPullRequest> queue = new(pending);

        public List<(Guid RunId, FixPullRequestRecord Record)> Published { get; } = [];

        public List<(Guid RunId, string Reason)> Skipped { get; } = [];

        public List<(Guid RunId, TimeSpan Delay)> Deferred { get; } = [];

        public List<(Guid RunId, string Reason)> Failed { get; } = [];

        public List<(Guid RunId, string HeadSha, IReadOnlyList<FixCheck> Checks)> Heads { get; } = [];

        public OpenedFixPullRequest? Opened { get; set; }

        public Task<IReadOnlyList<PendingFixPullRequest>> ClaimAsync(int batchSize, TimeSpan lease, CancellationToken cancellationToken)
        {
            var claimed = new List<PendingFixPullRequest>();
            while (claimed.Count < batchSize && queue.TryDequeue(out var next))
            {
                claimed.Add(next);
            }

            return Task.FromResult<IReadOnlyList<PendingFixPullRequest>>(claimed);
        }

        public Task MarkPublishedAsync(Guid runId, FixPullRequestRecord record, CancellationToken cancellationToken)
        {
            Published.Add((runId, record));
            return Task.CompletedTask;
        }

        public Task SkipAsync(Guid runId, string reason, CancellationToken cancellationToken)
        {
            Skipped.Add((runId, reason));
            return Task.CompletedTask;
        }

        public Task DeferAsync(Guid runId, TimeSpan delay, CancellationToken cancellationToken)
        {
            Deferred.Add((runId, delay));
            return Task.CompletedTask;
        }

        public Task FailAsync(Guid runId, string reason, CancellationToken cancellationToken)
        {
            Failed.Add((runId, reason));
            return Task.CompletedTask;
        }

        public Task<OpenedFixPullRequest?> FindOpenedAsync(
            string pullRequestId, IReadOnlyList<string> repositoryNames, CancellationToken cancellationToken) =>
            Task.FromResult(
                Opened is { } opened && opened.PullRequestId == pullRequestId && repositoryNames.Contains(opened.RepositoryId)
                    ? opened
                    : null);

        public Task MarkHeadAsync(Guid runId, string headSha, IReadOnlyList<FixCheck> checks, CancellationToken cancellationToken)
        {
            Heads.Add((runId, headSha, checks));
            if (Opened is { } opened)
            {
                Opened = opened with { CurrentHeadSha = headSha };
            }

            return Task.CompletedTask;
        }
    }

    internal sealed record Sent(HttpMethod Method, string Uri, string? Authorization, string? Body);

    /// <summary>Answers requests in order from prepared replies; records what was sent.</summary>
    internal sealed class FakeHandler(params object[] replies) : HttpMessageHandler
    {
        private readonly Queue<object> replies = new(replies);

        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Sent(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return replies.Dequeue() switch
            {
                HttpResponseMessage response => response,
                Exception error => throw error,
                var other => throw new InvalidOperationException($"Unexpected reply {other}"),
            };
        }
    }
}
