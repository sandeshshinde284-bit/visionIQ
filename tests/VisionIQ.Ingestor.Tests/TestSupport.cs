using System.Net;
using System.Net.Http.Headers;
using System.Text;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Tests;

internal static class Fixture
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

/// <summary>Answers Azure DevOps calls from recorded responses and records what was sent.</summary>
internal sealed class FakeAdoHandler : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string PathEnd, string? QueryContains, Func<HttpRequestMessage, HttpResponseMessage> Respond)> routes = [];

    public List<string> Requests { get; } = [];

    public List<string?> Bodies { get; } = [];

    public List<string?> Authorizations { get; } = [];

    public FakeAdoHandler On(HttpMethod method, string pathEnd, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        routes.Add((method, pathEnd, null, respond));
        return this;
    }

    /// <summary>A route that also requires the query string to contain <paramref name="queryContains"/>. Register these before the plain route.</summary>
    public FakeAdoHandler OnQuery(HttpMethod method, string pathEnd, string queryContains, string fixture)
    {
        routes.Add((method, pathEnd, queryContains, _ => Ok(Fixture.Read(fixture))));
        return this;
    }

    public FakeAdoHandler Json(HttpMethod method, string pathEnd, string fixture) => On(method, pathEnd, _ => Ok(Fixture.Read(fixture)));

    public static HttpResponseMessage Ok(string json, string? continuationToken = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (continuationToken is not null)
        {
            response.Headers.Add("x-ms-continuationtoken", continuationToken);
        }

        return response;
    }

    public static HttpResponseMessage Status(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("{\"message\":\"fake\"}", Encoding.UTF8, "application/json") };
        if (retryAfter is { } delay)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        }

        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
        Authorizations.Add(request.Headers.Authorization?.ToString());
        Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));

        foreach (var route in routes)
        {
            if (route.Method == request.Method
                && request.RequestUri.AbsolutePath.EndsWith(route.PathEnd, StringComparison.Ordinal)
                && (route.QueryContains is null || request.RequestUri.Query.Contains(route.QueryContains, StringComparison.Ordinal)))
            {
                return route.Respond(request);
            }
        }

        return Status(HttpStatusCode.NotFound);
    }
}

/// <summary>A small recorded Azure DevOps project: two work items, two pull requests, three commits, one build.</summary>
internal static class DemoAdo
{
    public const string Org = "https://dev.azure.com/contoso";
    public const string Project = "VisionIQ Demo";
    public const string Commit = "a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6a7b8c9d0";
    public const string MergeCommit = "f00dbabe00000000000000000000000000000077";
    public const string RevertCommit = "c0ffee0000000000000000000000000000000003";

    public static RepositoryRecord Repository { get; } = new("repo-1", "payments-service", Project, "refs/heads/main");

    public static AdoOptions Options(IReadOnlyList<string>? repositories = null) => new()
    {
        OrganizationUrl = new Uri(Org),
        Projects = [Project],
        Repositories = repositories ?? [],
        PersonalAccessToken = "test-token-not-real",
        MaxRetries = 2,
    };

    public static FakeAdoHandler Server(bool pipelinesDenied = false)
    {
        var handler = new FakeAdoHandler()
            .Json(HttpMethod.Post, "/_apis/wit/wiql", "wiql.json")
            .Json(HttpMethod.Post, "/_apis/wit/workitemsbatch", "workitems-batch.json")
            .Json(HttpMethod.Get, "/workItems/1234/revisions", "workitem-1234-revisions.json")
            .Json(HttpMethod.Get, "/workItems/1240/revisions", "workitem-1240-revisions.json")
            .Json(HttpMethod.Get, "/_apis/git/repositories", "repositories.json")
            .Json(HttpMethod.Get, "/repositories/repo-1/pullrequests", "pullrequests.json")
            .Json(HttpMethod.Get, "/pullRequests/77/workitems", "pr-77-workitems.json")
            .Json(HttpMethod.Get, "/pullRequests/77/commits", "pr-77-commits.json")
            .Json(HttpMethod.Get, "/pullRequests/78/workitems", "pr-78-workitems.json")
            .Json(HttpMethod.Get, "/pullRequests/78/commits", "pr-78-commits.json")
            .Json(HttpMethod.Get, "/repositories/repo-1/commits", "commits.json")
            .Json(HttpMethod.Get, "/changes", "commit-changes.json")
            .Json(HttpMethod.Get, "/commits/" + Commit, "commit-a1b2-detail.json")
            .Json(HttpMethod.Get, "/builds/501/artifacts", "build-501-artifacts.json")
            .Json(HttpMethod.Get, "/_apis/projects", "projects.json")
            .Json(HttpMethod.Get, "/_apis/build/definitions", "definitions.json");

        return pipelinesDenied
            ? handler.On(HttpMethod.Get, "/_apis/build/builds", _ => FakeAdoHandler.Status(HttpStatusCode.Unauthorized))
            : handler.Json(HttpMethod.Get, "/_apis/build/builds", "builds.json");
    }

    public static AdoClient Client(FakeAdoHandler handler, AdoOptions? options = null, List<TimeSpan>? delays = null) =>
        new(new HttpClient(handler), options ?? Options(), (delay, _) =>
        {
            delays?.Add(delay);
            return Task.CompletedTask;
        });
}

internal sealed class MemorySink : IRecordSink
{
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);

    public List<(string Stream, string Key, object Record)> Items { get; } = [];

    public Task WriteAsync(string stream, string key, object record, CancellationToken cancellationToken = default)
    {
        if (seen.Add(stream + "|" + key))
        {
            Items.Add((stream, key, record));
        }

        return Task.CompletedTask;
    }

    public int Count(string stream) => Items.Count(item => item.Stream == stream);
}
