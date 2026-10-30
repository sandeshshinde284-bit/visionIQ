using System.Net;
using System.Text;
using System.Text.Json;
using VisionIQ.Ingestor.Checkpoints;
using VisionIQ.Ingestor.Configuration;
using VisionIQ.Ingestor.Confluence;
using VisionIQ.Ingestor.Sources;
using VisionIQ.Ingestor.Sync;

namespace VisionIQ.Ingestor.Tests;

internal static class DemoConfluence
{
    public static ConfluenceOptions CloudOptions() => new()
    {
        BaseUrl = new Uri("https://contoso.atlassian.net/wiki"),
        SpaceKeys = ["ENG"],
        Deployment = ConfluenceDeployment.Cloud,
        UserEmail = "ci@contoso.com",
        ApiToken = "conf-token-not-real",
        MaxRetries = 1,
    };

    public static ConfluenceOptions DataCenterOptions() => new()
    {
        BaseUrl = new Uri("https://confluence.example.com/confluence"),
        SpaceKeys = ["OPS"],
        Deployment = ConfluenceDeployment.DataCenter,
        ApiToken = "dc-pat-not-real",
        MaxRetries = 1,
    };

    public static FakeAdoHandler CloudServer(string restriction1000 = "conf-cloud-restriction-none.json", bool spaceMissing = false, bool spacesDenied = false)
    {
        var handler = new FakeAdoHandler();
        if (spacesDenied)
        {
            handler.On(HttpMethod.Get, "/wiki/api/v2/spaces", _ => FakeAdoHandler.Status(HttpStatusCode.Unauthorized));
        }
        else
        {
            handler.Json(HttpMethod.Get, "/wiki/api/v2/spaces", spaceMissing ? "conf-cloud-spaces-empty.json" : "conf-cloud-spaces.json");
        }

        return handler
            .OnQuery(HttpMethod.Get, "/api/v2/spaces/98304/pages", "cursor=abc123", "conf-cloud-pages-2.json")
            .Json(HttpMethod.Get, "/api/v2/spaces/98304/pages", "conf-cloud-pages-1.json")
            .Json(HttpMethod.Get, "/content/1000/restriction/byOperation/read", restriction1000)
            .Json(HttpMethod.Get, "/content/1002/restriction/byOperation/read", "conf-cloud-restriction-1002.json")
            .Json(HttpMethod.Get, "/restriction/byOperation/read", "conf-cloud-restriction-none.json")
            .Json(HttpMethod.Get, "/pages/1001/labels", "conf-cloud-labels-1001.json")
            .Json(HttpMethod.Get, "/labels", "conf-cloud-labels-none.json")
            .Json(HttpMethod.Get, "/versions", "conf-cloud-versions.json");
    }

    public static FakeAdoHandler DataCenterServer() => new FakeAdoHandler()
        .Json(HttpMethod.Get, "/confluence/rest/api/space/OPS", "conf-dc-space.json")
        .Json(HttpMethod.Get, "/confluence/rest/api/content", "conf-dc-content.json");

    public static IConfluenceSource Source(FakeAdoHandler handler, ConfluenceOptions options) =>
        ConfluenceSources.Create(new HttpClient(handler), options, (_, _) => Task.CompletedTask);

    public static ConfluencePage Page(IEnumerable<ConfluencePage> pages, string id) => pages.Single(page => page.Id == id);
}

public sealed class ConfluenceTests
{
    // ---------- S04-08: page snapshot connector ----------

    [Fact]
    public async Task Cloud_pages_are_read_across_result_pages_with_their_restrictions()
    {
        var handler = DemoConfluence.CloudServer();

        var pages = await DemoConfluence.Source(handler, DemoConfluence.CloudOptions()).GetPagesAsync("ENG");

        Assert.Equal(new[] { "1000", "1001", "1002", "1003", "1004" }, pages.Select(page => page.Id));
        Assert.Contains(handler.Requests, request => request.Contains("cursor=abc123", StringComparison.Ordinal));
        var restricted = DemoConfluence.Page(pages, "1002").ReadRestriction;
        Assert.Equal(new[] { "acc-lead" }, restricted.Users);
        Assert.Equal(new[] { "eng-leads" }, restricted.Groups);
        Assert.Equal("https://contoso.atlassian.net/wiki/spaces/ENG/pages/1001/ADR-012", DemoConfluence.Page(pages, "1001").Url);
        Assert.Equal(3, DemoConfluence.Page(pages, "1001").Version);
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ci@contoso.com:conf-token-not-real"));
        Assert.Equal(expected, handler.Authorizations[0]);
    }

    [Fact]
    public async Task Cloud_enrichment_adds_labels_and_the_version_history_oldest_first()
    {
        var source = DemoConfluence.Source(DemoConfluence.CloudServer(), DemoConfluence.CloudOptions());
        var page = DemoConfluence.Page(await source.GetPagesAsync("ENG"), "1001");

        var enriched = await source.EnrichAsync(page);

        Assert.Equal(new[] { "adr" }, enriched.Labels);
        Assert.Equal(new[] { 1, 2, 3 }, enriched.Versions.Select(version => version.Number));
    }

    [Fact]
    public void Truncated_or_missing_restriction_lists_are_unknown_never_empty()
    {
        using var truncated = JsonDocument.Parse(Fixture.Read("conf-cloud-restriction-truncated.json"));
        using var missing = JsonDocument.Parse("{\"operation\":\"read\"}");

        Assert.True(ConfluenceCloudSource.ParseRestriction(truncated.RootElement).Unknown);
        Assert.True(ConfluenceCloudSource.ParseRestriction(missing.RootElement).Unknown);
    }

    [Fact]
    public async Task A_restriction_call_that_fails_makes_the_page_unknown()
    {
        var handler = new FakeAdoHandler()
            .Json(HttpMethod.Get, "/wiki/api/v2/spaces", "conf-cloud-spaces.json")
            .OnQuery(HttpMethod.Get, "/api/v2/spaces/98304/pages", "cursor=abc123", "conf-cloud-pages-2.json")
            .Json(HttpMethod.Get, "/api/v2/spaces/98304/pages", "conf-cloud-pages-1.json")
            .On(HttpMethod.Get, "/content/1001/restriction/byOperation/read", _ => FakeAdoHandler.Status(HttpStatusCode.InternalServerError))
            .Json(HttpMethod.Get, "/restriction/byOperation/read", "conf-cloud-restriction-none.json");

        var pages = await DemoConfluence.Source(handler, DemoConfluence.CloudOptions()).GetPagesAsync("ENG");

        Assert.True(DemoConfluence.Page(pages, "1001").ReadRestriction.Unknown);
        Assert.False(ConfluenceAccess.ComputeAcls(pages)["1001"].Complete);
    }

    [Fact]
    public async Task A_missing_space_is_reported_as_not_found()
    {
        var source = DemoConfluence.Source(DemoConfluence.CloudServer(spaceMissing: true), DemoConfluence.CloudOptions());

        var error = await Assert.ThrowsAsync<ConfluenceSourceException>(() => source.GetPagesAsync("ENG"));

        Assert.True(error.IsNotFound);
        Assert.Contains("ENG", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Data_center_pages_come_with_ancestors_labels_and_restrictions()
    {
        var handler = DemoConfluence.DataCenterServer();

        var pages = await DemoConfluence.Source(handler, DemoConfluence.DataCenterOptions()).GetPagesAsync("OPS");

        var child = DemoConfluence.Page(pages, "2002");
        Assert.Equal(new[] { "2001" }, child.AncestorIds);
        Assert.Equal(new[] { "decision" }, child.Labels);
        Assert.Equal(new[] { "jdoe" }, child.ReadRestriction.Users);
        Assert.Equal(new[] { "architects" }, DemoConfluence.Page(pages, "2001").ReadRestriction.Groups);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.Zero), DemoConfluence.Page(pages, "2001").LastModified);
        Assert.Equal("Bearer dc-pat-not-real", handler.Authorizations[0]);
        Assert.Contains(handler.Requests, request => request.Contains("expand=", StringComparison.Ordinal));
    }

    // ---------- S04-09: permission mapping (fail closed) ----------

    [Fact]
    public async Task Restrictions_are_inherited_and_unclear_ancestry_hides_the_page()
    {
        var pages = await DemoConfluence.Source(DemoConfluence.CloudServer(), DemoConfluence.CloudOptions()).GetPagesAsync("ENG");

        var acls = ConfluenceAccess.ComputeAcls(pages);

        Assert.False(acls["1000"].IsRestricted);
        Assert.Equal(new[] { "1002" }, acls["1003"].Levels.Select(level => level.PageId));
        Assert.False(acls["1004"].Complete);
        Assert.Contains("9999", acls["1004"].IncompleteReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_restricted_page_is_hidden_from_an_unauthorized_engineer()
    {
        var pages = await DemoConfluence.Source(DemoConfluence.CloudServer(), DemoConfluence.CloudOptions()).GetPagesAsync("ENG");
        var acls = ConfluenceAccess.ComputeAcls(pages);

        string[] VisibleTo(ConfluenceViewer viewer) =>
            ConfluenceAccess.Visible(pages, page => acls[page.Id], viewer).Select(page => page.Id).ToArray();

        Assert.Equal(new[] { "1000", "1001" }, VisibleTo(ConfluenceViewer.Create(null, [], ["ENG"])));
        Assert.Equal(new[] { "1000", "1001", "1002", "1003" }, VisibleTo(ConfluenceViewer.Create("acc-lead", [], ["ENG"])));
        Assert.Equal(new[] { "1000", "1001", "1002", "1003" }, VisibleTo(ConfluenceViewer.Create("someone", ["ENG-LEADS"], ["eng"])));
        Assert.Empty(VisibleTo(ConfluenceViewer.Create("acc-lead", ["eng-leads"], [])));
    }

    [Fact]
    public async Task A_viewer_must_pass_every_restricted_level()
    {
        var pages = await DemoConfluence.Source(DemoConfluence.DataCenterServer(), DemoConfluence.DataCenterOptions()).GetPagesAsync("OPS");
        var acl = ConfluenceAccess.ComputeAcls(pages)["2002"];

        Assert.False(ConfluenceAccess.CanView(acl, ConfluenceViewer.Create("jdoe", [], ["OPS"])));
        Assert.False(ConfluenceAccess.CanView(acl, ConfluenceViewer.Create("alice", ["architects"], ["OPS"])));
        Assert.True(ConfluenceAccess.CanView(acl, ConfluenceViewer.Create("jdoe", ["architects"], ["OPS"])));
    }

    // ---------- S05-07: normalization and design decisions ----------

    [Fact]
    public void Storage_format_becomes_text_sections_and_properties()
    {
        using var document = JsonDocument.Parse(Fixture.Read("conf-cloud-pages-1.json"));
        var body = document.RootElement.GetProperty("results")[1].GetProperty("body").GetProperty("storage").GetProperty("value").GetString();

        var page = ConfluenceText.Normalize(body);

        Assert.Equal("Accepted", page.Properties["status"]);
        Assert.Equal("Cap retries at 3 with a budget per request.", page.Properties["Outcome"]);
        Assert.Equal(new[] { string.Empty, "Context", "Decision" }, page.Sections.Select(section => section.Heading));
        var context = page.Sections[1].Text;
        Assert.Contains("Retries & timeouts caused storms.", context, StringComparison.Ordinal);
        Assert.Contains("if (attempt > 3) return;", context, StringComparison.Ordinal);
        Assert.DoesNotContain("csharp", page.Text, StringComparison.Ordinal);
        Assert.Equal("- Use a retry budget\n- Log when exhausted", page.Sections[2].Text);
    }

    [Fact]
    public void Decisions_are_recognized_by_label_title_or_template_and_say_why()
    {
        var acl = new ConfluencePageAcl("1", "ENG", true, []);
        ConfluencePageSnapshot Snapshot(string title, string[] labels, Dictionary<string, string> properties) =>
            new("contoso.atlassian.net", "ENG", "1", 1, title, DateTimeOffset.UnixEpoch) { Labels = labels, Properties = properties, Acl = acl };
        var template = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Status"] = "Accepted", ["Outcome"] = "Use Redis" };

        var byLabel = DesignDecisionDetector.Detect(Snapshot("Retry budget", ["ADR"], []));
        var byTitle = DesignDecisionDetector.Detect(Snapshot("Decision: Use Redis for sessions", [], []));
        var byTemplate = DesignDecisionDetector.Detect(Snapshot("Session storage", [], template));
        var notADecision = DesignDecisionDetector.Detect(Snapshot("Team onboarding", ["howto"], []));

        Assert.Equal("label:adr", byLabel!.DetectedBy);
        Assert.Equal("title", byTitle!.DetectedBy);
        Assert.Equal("decision template", byTemplate!.DetectedBy);
        Assert.Equal("Use Redis", byTemplate.Outcome);
        Assert.Equal("Accepted", byTemplate.Status);
        Assert.Null(notADecision);
    }

    // ---------- the runner: versions, permissions every run, failures ----------

    [Fact]
    public async Task The_first_run_stores_every_page_version_with_its_permissions()
    {
        var sink = new MemorySink();

        var report = await Runner(DemoConfluence.CloudServer(), new InMemoryCheckpointStore(), sink).RunAsync(Request());

        var result = Assert.Single(report.Streams);
        Assert.Equal(StreamStatus.Partial, result.Status);
        Assert.Equal(2, sink.Count("confluence-pages"));
        Assert.Equal(5, sink.Count("confluence-acl"));
        Assert.Equal(1, sink.Count("design-decisions"));
        Assert.Contains(result.Notes, note => note.Contains("1 page(s) have unclear permissions", StringComparison.Ordinal));
        var timelineAcl = Acl(sink, "1003");
        Assert.True(timelineAcl.IsRestricted);
        Assert.DoesNotContain(sink.Items, item => item.Stream == "confluence-pages" && item.Key.Contains("/1003@", StringComparison.Ordinal));
        var adr = Snapshot(sink, "1001");
        Assert.Equal(new[] { 1, 2, 3 }, adr.Versions.Select(version => version.Number));
        Assert.Contains(sink.Items, item => item.Key.EndsWith("/page/1001@3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_run_rereads_permissions_but_not_unchanged_pages()
    {
        var checkpoints = new InMemoryCheckpointStore();
        await Runner(DemoConfluence.CloudServer(), checkpoints, new MemorySink()).RunAsync(Request());
        var second = new MemorySink();

        await Runner(DemoConfluence.CloudServer(), checkpoints, second).RunAsync(Request());

        // Withheld pages leave the checkpoint unchanged, so public data is
        // re-exported while permissions are rechecked on the next run.
        Assert.Equal(2, second.Count("confluence-pages"));
        Assert.Equal(1, second.Count("design-decisions"));
        Assert.Equal(5, second.Count("confluence-acl"));
    }

    [Fact]
    public async Task A_permission_change_without_a_new_page_version_is_picked_up()
    {
        var checkpoints = new InMemoryCheckpointStore();
        var first = new MemorySink();
        await Runner(DemoConfluence.CloudServer(), checkpoints, first).RunAsync(Request());
        var second = new MemorySink();

        await Runner(DemoConfluence.CloudServer(restriction1000: "conf-cloud-restriction-1000-changed.json"), checkpoints, second).RunAsync(Request());

        var before = Acl(first, "1000");
        var after = Acl(second, "1000");
        Assert.False(before.IsRestricted);
        Assert.True(after.IsRestricted);
        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        Assert.False(ConfluenceAccess.CanView(after, ConfluenceViewer.Create(null, [], ["ENG"])));
        // Changing an ancestor to restricted also withholds its descendants.
        Assert.Equal(0, second.Count("confluence-pages"));
        Assert.DoesNotContain(second.Items, item => item.Stream == "confluence-pages" && item.Key.Contains("/1000@", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_space_that_cannot_be_read_is_not_synced()
    {
        var report = await Runner(DemoConfluence.CloudServer(spacesDenied: true), new InMemoryCheckpointStore(), new MemorySink()).RunAsync(Request());

        var result = Assert.Single(report.Streams);
        Assert.Equal(StreamStatus.NotSynced, result.Status);
        Assert.Contains("Access denied (401)", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_from_both_runners_merge_into_one()
    {
        var confluence = await Runner(DemoConfluence.CloudServer(), new InMemoryCheckpointStore(), new MemorySink()).RunAsync(Request());
        var ado = await new SyncRunner(DemoAdo.Client(DemoAdo.Server()), new InMemoryCheckpointStore(), new MemorySink(), new SourceCapabilityRegistry())
            .RunAsync(new SyncRequest(new HashSet<SyncKind> { SyncKind.Builds }, null));

        var merged = SyncReport.Merge([ado, confluence]);

        Assert.Equal(2, merged.Streams.Count);
        Assert.False(merged.AllSynced);
        Assert.Equal("contoso, contoso.atlassian.net", merged.Tenant);
    }

    // ---------- settings and command line ----------

    [Fact]
    public void Cloud_is_detected_from_the_address_and_needs_an_email()
    {
        var values = Settings();
        values.Remove("CONFLUENCE_USER_EMAIL");

        var error = Assert.Throws<InvalidOperationException>(() => IngestorSettings.FromValues(values));

        Assert.Contains("CONFLUENCE_USER_EMAIL", error.Message, StringComparison.Ordinal);
        values["CONFLUENCE_USER_EMAIL"] = "ci@contoso.com";
        var confluence = IngestorSettings.FromValues(values).Confluence!;
        Assert.Equal(ConfluenceDeployment.Cloud, confluence.Deployment);
        Assert.Equal("https://contoso.atlassian.net/wiki", confluence.ApiRoot);
        Assert.DoesNotContain("conf-secret", confluence.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Data_center_keeps_its_context_path_and_needs_no_email()
    {
        var values = Settings();
        values["CONFLUENCE_BASE_URL"] = "https://confluence.example.com/confluence";
        values.Remove("CONFLUENCE_USER_EMAIL");

        var confluence = IngestorSettings.FromValues(values).Confluence!;

        Assert.Equal(ConfluenceDeployment.DataCenter, confluence.Deployment);
        Assert.Equal("https://confluence.example.com/confluence", confluence.ApiRoot);
    }

    [Fact]
    public void Confluence_is_optional_but_partial_settings_are_an_error()
    {
        var values = Settings();
        foreach (var key in values.Keys.Where(key => key.StartsWith("CONFLUENCE_", StringComparison.Ordinal)).ToList())
        {
            values.Remove(key);
        }

        Assert.Null(IngestorSettings.FromValues(values).Confluence);
        values["CONFLUENCE_SPACE_KEYS"] = "ENG";
        var error = Assert.Throws<InvalidOperationException>(() => IngestorSettings.FromValues(values));
        Assert.Contains("CONFLUENCE_BASE_URL", error.Message, StringComparison.Ordinal);
        Assert.Contains("CONFLUENCE_API_TOKEN", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_confluence_source_can_be_requested_explicitly()
    {
        var explicitOptions = CommandLine.Parse(["sync", "--source", "confluence"]);
        var defaultOptions = CommandLine.Parse(["sync"]);

        Assert.True(explicitOptions.SourceExplicit);
        Assert.True(explicitOptions.Kinds.SetEquals(new[] { SyncKind.Confluence }));
        Assert.False(defaultOptions.SourceExplicit);
        Assert.Contains(SyncKind.Confluence, defaultOptions.Kinds);
    }

    private static SyncRequest Request() => new(new HashSet<SyncKind> { SyncKind.Confluence }, null);

    private static ConfluenceSyncRunner Runner(FakeAdoHandler handler, ICheckpointStore checkpoints, IRecordSink sink) =>
        new(DemoConfluence.Source(handler, DemoConfluence.CloudOptions()), checkpoints, sink, new SourceCapabilityRegistry());

    private static ConfluencePageSnapshot Snapshot(MemorySink sink, string id) =>
        sink.Items.Where(item => item.Stream == "confluence-pages").Select(item => (ConfluencePageSnapshot)item.Record).Single(page => page.Id == id);

    private static ConfluencePageAcl Acl(MemorySink sink, string id) =>
        sink.Items.Where(item => item.Stream == "confluence-acl").Select(item => (ConfluencePageAcl)item.Record).Single(acl => acl.PageId == id);

    private static Dictionary<string, string?> Settings() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["ADO_ORG_URL"] = "https://dev.azure.com/contoso",
        ["ADO_PROJECTS"] = "VisionIQ Demo",
        ["ADO_READ_PAT"] = "ado-secret",
        ["CHECKPOINT_STORE"] = "file",
        ["CONFLUENCE_BASE_URL"] = "https://contoso.atlassian.net/wiki",
        ["CONFLUENCE_SPACE_KEYS"] = "ENG",
        ["CONFLUENCE_USER_EMAIL"] = "ci@contoso.com",
        ["CONFLUENCE_API_TOKEN"] = "conf-secret",
    };
}
