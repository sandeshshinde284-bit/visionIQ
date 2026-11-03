using System.Text;
using System.Web;
using VisionIQ.Ingestor.Ado;

namespace VisionIQ.Ingestor.Tests;

/// <summary>M-6: every changed file is read; truncation is flagged, never silent.</summary>
public sealed class CommitChangesPagingTests
{
    private static readonly RepositoryRecord Repo = new("repo-1", "payments-service", "VisionIQ Demo", "refs/heads/main");

    private static CommitRecord Commit() =>
        new(new string('a', 40), "repo-1", "VisionIQ Demo", DateTimeOffset.UnixEpoch, "big change");

    /// <summary>Serves <paramref name="total"/> changed files in pages, honouring top/skip.</summary>
    private static FakeAdoHandler Server(int total) => new FakeAdoHandler().On(HttpMethod.Get, "/changes", request =>
    {
        var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
        var skip = int.Parse(query["skip"] ?? "0");
        var top = int.Parse(query["top"] ?? "100");
        var json = new StringBuilder("{\"changes\":[");
        for (var i = skip; i < Math.Min(total, skip + top); i++)
        {
            json.Append(i > skip ? "," : string.Empty)
                .Append("{\"item\":{\"path\":\"/f").Append(i).Append(".cs\",\"gitObjectType\":\"blob\"},\"changeType\":\"add\"}");
        }

        return FakeAdoHandler.Ok(json.Append("]}").ToString());
    });

    [Fact]
    public async Task More_than_1000_changed_files_are_all_read()
    {
        var enriched = await new AdoCommitSource(DemoAdo.Client(Server(2_345))).EnrichAsync("VisionIQ Demo", Repo, Commit());

        Assert.Equal(2_345, enriched.ChangedFiles.Count);
        Assert.False(enriched.ChangedFilesTruncated);
    }

    [Fact]
    public async Task Beyond_the_cap_the_commit_is_flagged_as_truncated()
    {
        var enriched = await new AdoCommitSource(DemoAdo.Client(Server(AdoCommitSource.MaxChangedFiles + 10))).EnrichAsync("VisionIQ Demo", Repo, Commit());

        Assert.Equal(AdoCommitSource.MaxChangedFiles, enriched.ChangedFiles.Count);
        Assert.True(enriched.ChangedFilesTruncated);
    }

    [Fact]
    public void The_flag_is_omitted_from_stored_json_when_false() =>
        Assert.DoesNotContain("changedFilesTruncated",
            System.Text.Json.JsonSerializer.Serialize(Commit(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            StringComparison.Ordinal);
}
