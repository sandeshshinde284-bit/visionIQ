using VisionIQ.Ingestor.Ado;

namespace VisionIQ.Ingestor.Tests;

public sealed class AdoWorkItemReaderTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Wiql_for_a_project_without_a_checkpoint_has_no_date_filter()
    {
        var query = AdoWorkItemReader.BuildWiql("VisionIQ Demo", null);

        Assert.Equal(
            "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = 'VisionIQ Demo' ORDER BY [System.ChangedDate] ASC, [System.Id] ASC",
            query);
    }

    [Fact]
    public void Wiql_resumes_from_the_checkpoint_time()
    {
        var query = AdoWorkItemReader.BuildWiql("VisionIQ Demo", new DateTimeOffset(2026, 10, 20, 10, 15, 0, TimeSpan.Zero));

        Assert.Contains("[System.ChangedDate] >= '2026-10-20T10:15:00.000Z'", query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Demo'; DROP")]
    [InlineData("Demo\" OR 1=1")]
    [InlineData("")]
    public void Project_names_that_could_alter_the_query_are_rejected(string project)
    {
        Assert.ThrowsAny<ArgumentException>(() => AdoWorkItemReader.BuildWiql(project, null));
    }


    [Theory]
    [InlineData("Bug")]
    [InlineData("User Story")]
    [InlineData("Task")]
    [InlineData("Test Case")]
    [InlineData("Feature")]
    [InlineData("Test Suite")]
    public void Source_work_item_type_and_title_are_preserved_without_reclassification(string type)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = 42,
            rev = 3,
            fields = new Dictionary<string, object>
            {
                ["System.WorkItemType"] = type,
                ["System.Title"] = "Checkout failure regression",
                ["System.State"] = "Active",
                ["System.ChangedDate"] = "2026-10-01T04:55:24.323Z"
            }
        });

        var outcome = AdoWorkItemReader.ParseWorkItem(payload);

        Assert.True(outcome.IsSynced);
        Assert.Equal(type, outcome.Record!.WorkItemType);
        Assert.Equal("Checkout failure regression", outcome.Record.Title);
        Assert.Equal(3, outcome.Record.Revision);
    }

    [Fact]
    public void Historical_revision_preserves_original_state_title_and_reason()
    {
        using var json = System.Text.Json.JsonDocument.Parse("""
            {
              "id": 42,
              "rev": 2,
              "fields": {
                "System.ChangedDate": "2026-10-01T04:55:24.323Z",
                "System.State": "Resolved",
                "System.Title": "Earlier checkout failure",
                "System.Reason": "Fixed"
              }
            }
            """);

        var revision = AdoWorkItemReader.ParseRevision(json.RootElement);

        Assert.NotNull(revision);
        Assert.Equal(42, revision.Id);
        Assert.Equal(2, revision.Revision);
        Assert.Equal("Resolved", revision.State);
        Assert.Equal("Earlier checkout failure", revision.Title);
        Assert.Equal("Fixed", revision.Reason);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T04:55:24.323Z"), revision.ChangedDate);
    }

    [Fact]
    public void Recorded_work_item_is_parsed_into_a_record()
    {
        var outcome = AdoWorkItemReader.ParseWorkItem(Fixture("workitem-1234.json"));

        Assert.True(outcome.IsSynced);
        Assert.Null(outcome.NotSyncedReason);
        Assert.Equal(1234, outcome.Record!.Id);
        Assert.Equal(7, outcome.Record.Revision);
        Assert.Equal("Bug", outcome.Record.WorkItemType);
        Assert.Equal(new[] { "payments", "retry" }, outcome.Record.Tags);
    }

    [Fact]
    public void Missing_revision_is_not_synced_rather_than_guessed()
    {
        var outcome = AdoWorkItemReader.ParseWorkItem("{\"id\": 1, \"fields\": {}}");

        Assert.False(outcome.IsSynced);
        Assert.Equal("Missing or invalid revision.", outcome.NotSyncedReason);
    }

    [Fact]
    public void Missing_required_field_is_not_synced()
    {
        var outcome = AdoWorkItemReader.ParseWorkItem(
            "{\"id\": 1, \"rev\": 2, \"fields\": {\"System.WorkItemType\": \"Bug\", \"System.State\": \"New\", \"System.ChangedDate\": \"2026-10-20T10:15:00Z\"}}");

        Assert.False(outcome.IsSynced);
        Assert.Contains("Missing required field", outcome.NotSyncedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_json_is_not_synced()
    {
        Assert.Equal("Response is not valid JSON.", AdoWorkItemReader.ParseWorkItem("<html>503</html>").NotSyncedReason);
    }

    [Fact]
    public void Empty_body_is_not_synced()
    {
        Assert.False(AdoWorkItemReader.ParseWorkItem("  ").IsSynced);
    }
}
