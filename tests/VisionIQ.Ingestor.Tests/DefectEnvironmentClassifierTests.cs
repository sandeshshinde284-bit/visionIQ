using System.Diagnostics;
using System.Text.Json;
using VisionIQ.Ingestor.Ado;
using VisionIQ.Ingestor.Configuration;
using VisionIQ.Ingestor.Defects;

namespace VisionIQ.Ingestor.Tests;

/// <summary>
/// Optional, provider-independent defect environment classification across ADO and Jira projects
/// with different conventions. Never guesses, never blocks, keeps provenance.
/// </summary>
public sealed class DefectEnvironmentClassifierTests
{
    private const string Rules = """
    {
      "providers": {
        "azure_devops": { "projects": {
          "Payments":  { "sources": [ { "type": "field", "name": "Custom.FoundInEnvironment" } ],
                         "values": { "PROD": ["Production", "Live"], "UAT": ["UAT", "Staging"], "TEST": ["QA"] } },
          "Ticketing": { "sources": [ { "type": "title", "pattern": "^\\[(?<env>PROD|UAT|TEST)\\]", "trust": "verified" } ],
                         "values": { "PROD": ["PROD"], "UAT": ["UAT"], "TEST": ["TEST"] } },
          "Mobile":    { "sources": [ { "type": "labels", "prefix": "env:" }, { "type": "title", "pattern": "\\b(?<env>prod|uat)\\b" } ],
                         "values": { "PROD": ["prod"], "UAT": ["uat"], "TEST": ["test"] } },
          "Platform":  { "sources": [ { "type": "path" } ],
                         "values": { "PROD": ["Prod Support"], "UAT": ["UAT Defects"], "TEST": ["QA"] } },
          "Broken":    { "sources": [], "values": {} },
          "Slow":      { "sources": [ { "type": "title", "pattern": "^(a+)+$(?<env>x)" } ], "values": { "PROD": ["x"] } }
        } },
        "jira": { "projects": {
          "CORE": { "sources": [ { "type": "labels" } ],
                    "values": { "PROD": ["production"], "UAT": ["uat"], "TEST": ["test"] } },
          "WEB":  { "sources": [ { "type": "field", "name": "environment" },
                                 { "type": "description", "pattern": "observed in (?<env>\\w+)" } ],
                    "values": { "PROD": ["Production", "PROD"], "UAT": ["UAT"], "TEST": ["TEST"] } },
          "API":  { "sources": [ { "type": "field", "name": "customfield_10050" }, { "type": "labels" } ],
                    "values": { "PROD": ["Production", "prod"], "UAT": ["uat"], "TEST": ["test"] } }
        } }
      }
    }
    """;

    private static readonly Dictionary<string, IReadOnlyCollection<string>> Approved = new()
    {
        ["azure_devops"] = ["Payments", "Ticketing", "Mobile", "Platform", "Broken", "Slow", "Legacy"],
        ["jira"] = ["CORE", "WEB", "API", "OPS"],
    };

    private static readonly DefectEnvironmentClassifier Classifier = DefectEnvironmentClassifier.FromJson(Rules, Approved);

    private static EnvironmentClassification Classify(
        string provider, string project, string? title = null, string? description = null,
        Dictionary<string, string>? fields = null, string[]? labels = null, string[]? path = null) =>
        Classifier.Classify(new DefectEvidence(provider, project, title, description,
            fields ?? new Dictionary<string, string>(), labels ?? [], path ?? []));

    private static (string, string, string?, string, string) Summary(EnvironmentClassification c) =>
        (c.Normalized, c.Status, c.Raw, c.Source, c.Reason);

    // --- Different conventions per project -----------------------------------------------------

    [Fact]
    public void Ado_dedicated_field_is_verified_and_keeps_the_raw_value()
    {
        var result = Classify("azure_devops", "Payments", fields: new() { ["Custom.FoundInEnvironment"] = "Production" });

        Assert.Equal(("PROD", "verified", "Production", "field:Custom.FoundInEnvironment", "matched"), Summary(result));
        Assert.StartsWith("env-rules-", result.RulesVersion);
        Assert.Single(result.Findings);
    }

    [Fact]
    public void Ado_title_convention_declared_verified_by_the_project()
    {
        Assert.Equal(("PROD", "verified", "PROD", "title", "matched"), Summary(Classify("azure_devops", "Ticketing", title: "[PROD] Payment failure")));
    }

    [Fact]
    public void Ado_area_path_segment_matches_exactly()
    {
        Assert.Equal("PROD", Classify("azure_devops", "Platform", path: ["Platform", "Prod Support", "Payments"]).Normalized);
        Assert.Equal("no_value", Classify("azure_devops", "Platform", path: ["Platform", "Prod Support Archive"]).Reason);
    }

    [Fact]
    public void Jira_label_is_verified()
    {
        Assert.Equal(("PROD", "verified", "production", "labels", "matched"), Summary(Classify("jira", "CORE", labels: ["backend", "production"])));
    }

    [Fact]
    public void Jira_description_only_ever_yields_a_candidate()
    {
        var result = Classify("jira", "WEB", description: "Issue observed in TEST after the release");

        Assert.Equal(("TEST", "candidate", "TEST", "description", "matched"), Summary(result));
    }

    [Fact]
    public void A_prefixed_label_with_an_unknown_value_is_unmapped_but_unrelated_plain_labels_are_ignored()
    {
        Assert.Equal(("UNKNOWN", "unmapped_value"), (Classify("azure_devops", "Mobile", labels: ["env:preprod"]).Normalized,
            Classify("azure_devops", "Mobile", labels: ["env:preprod"]).Reason));
        Assert.Equal("no_value", Classify("jira", "CORE", labels: ["backend", "frontend"]).Reason);
    }

    // --- Trust and conflicts -----------------------------------------------------------------------

    [Fact]
    public void A_candidate_never_overrides_verified_evidence_but_is_kept_as_provenance()
    {
        var result = Classify("azure_devops", "Mobile", title: "Crash only in prod", labels: ["env:uat"]);

        Assert.Equal(("UAT", "verified", "uat", "labels", "matched"), Summary(result));
        Assert.Contains(result.Findings, f => f.Source == "title" && f.Trust == "candidate" && f.Normalized == "PROD");
    }

    [Fact]
    public void With_no_verified_source_a_matching_title_is_a_candidate()
    {
        Assert.Equal(("PROD", "candidate"), (Classify("azure_devops", "Mobile", title: "Crash only in prod").Normalized,
            Classify("azure_devops", "Mobile", title: "Crash only in prod").Status));
    }

    [Fact]
    public void Reliable_sources_that_disagree_are_a_conflict_with_both_values_preserved()
    {
        var result = Classify("jira", "API", fields: new() { ["customfield_10050"] = "Production" }, labels: ["uat"]);

        Assert.Equal(("CONFLICT", "conflict", "sources_disagree"), (result.Normalized, result.Status, result.Reason));
        Assert.Contains("field:customfield_10050=Production", result.Raw, StringComparison.Ordinal);
        Assert.Contains("labels=uat", result.Raw, StringComparison.Ordinal);
        Assert.Equal(2, result.Findings.Count);
    }

    [Fact]
    public void An_uninterpretable_verified_value_is_unknown_not_a_fallback_guess()
    {
        var result = Classify("jira", "API", fields: new() { ["customfield_10050"] = "Pre-Prod" }, labels: ["prod"]);

        Assert.Equal(("UNKNOWN", "unknown", "Pre-Prod", "field:customfield_10050", "unmapped_value"), Summary(result));
    }

    // --- Missing data and configuration ------------------------------------------------------------

    [Fact]
    public void Missing_environment_is_unknown_and_never_an_error()
    {
        Assert.Equal(("UNKNOWN", "unknown", null, "none", "no_value"), Summary(Classify("azure_devops", "Payments", title: "Something broke")));
    }

    [Theory]
    [InlineData("azure_devops", "Legacy")]
    [InlineData("jira", "OPS")]
    public void Projects_without_rules_are_not_configured(string provider, string project) =>
        Assert.Equal(EnvironmentClassification.NotConfigured, Classify(provider, project, title: "[PROD] x", labels: ["production"]));

    [Fact]
    public void Invalid_rules_affect_only_their_own_project()
    {
        Assert.Equal("rules_invalid", Classify("azure_devops", "Broken", title: "[PROD] x").Reason);
        Assert.Equal("PROD", Classify("azure_devops", "Payments", fields: new() { ["Custom.FoundInEnvironment"] = "Live" }).Normalized);
        Assert.Contains(Classifier.Problems, p => p.Contains("azure_devops/Broken", StringComparison.Ordinal));
    }

    [Fact]
    public void Malformed_json_disables_classification_without_throwing()
    {
        var classifier = DefectEnvironmentClassifier.FromJson("{ not json", Approved);

        Assert.Single(classifier.Problems);
        Assert.Equal("not_configured", classifier.Classify(new DefectEvidence("jira", "CORE", null, null, new Dictionary<string, string>(), ["production"], [])).Reason);
    }

    [Theory]
    [InlineData("""{"type":"description","pattern":"in (?<env>\\w+)","trust":"verified"}""", "never verified")]
    [InlineData("""{"type":"title","pattern":"PROD"}""", "named group 'env'")]
    [InlineData("""{"type":"title","pattern":"(?<env>[unclosed"}""", "not a valid regular expression")]
    [InlineData("""{"type":"comments"}""", "unknown source type")]
    [InlineData("""{"type":"field","name":"Found In"}""", "valid field name")]
    [InlineData("""{"type":"field","name":"Custom.Env","trust":"maybe"}""", "trust must be")]
    public void Each_invalid_source_is_reported_for_its_project(string source, string message)
    {
        var json = """{"providers":{"jira":{"projects":{"CORE":{"sources":[""" + source + """],"values":{"PROD":["p"]}}}}}}""";

        Assert.Contains(DefectEnvironmentClassifier.FromJson(json, Approved).Problems, p => p.Contains(message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"PROD":["Live"],"UAT":["live"]}""", "maps to both")]
    [InlineData("""{"UNKNOWN":["?"]}""", "reserved")]
    [InlineData("""{"CONFLICT":["?"]}""", "reserved")]
    [InlineData("""{}""", "no values")]
    public void Invalid_value_mappings_are_reported(string values, string message)
    {
        var json = """{"providers":{"jira":{"projects":{"CORE":{"sources":[{"type":"labels"}],"values":""" + values + "}}}}}";

        Assert.Contains(DefectEnvironmentClassifier.FromJson(json, Approved).Problems, p => p.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_providers_and_unapproved_projects_are_reported_and_ignored()
    {
        const string Json = """{"providers":{"github":{"projects":{"x":{}}},"jira":{"projects":{"SECRET":{"sources":[{"type":"labels"}],"values":{"PROD":["p"]}}}}}}""";
        var problems = DefectEnvironmentClassifier.FromJson(Json, Approved).Problems;

        Assert.Contains(problems, p => p.Contains("unknown provider 'github'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("jira/SECRET", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pathological_title_cannot_stall_classification()
    {
        var timer = Stopwatch.StartNew();
        var result = Classify("azure_devops", "Slow", title: new string('a', 5_000) + "!");

        Assert.Equal("no_value", result.Reason);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"took {timer.Elapsed}");
    }

    [Fact]
    public void The_rules_version_ignores_value_order_but_changes_with_source_order()
    {
        string Version(string sources, string values) => DefectEnvironmentClassifier.FromJson(
                """{"providers":{"jira":{"projects":{"CORE":{"sources":""" + sources + ""","values":{"PROD":""" + values + "}}}}}}", Approved)
            .Classify(new DefectEvidence("jira", "CORE", null, null, new Dictionary<string, string> { ["environment"] = "p" }, ["p"], [])).RulesVersion!;
        const string FieldThenLabels = """[{"type":"field","name":"environment"},{"type":"labels"}]""";
        const string LabelsThenField = """[{"type":"labels"},{"type":"field","name":"environment"}]""";

        Assert.Equal(Version(FieldThenLabels, """["p","q"]"""), Version(FieldThenLabels, """["q","p"]"""));
        Assert.NotEqual(Version(FieldThenLabels, """["p"]"""), Version(LabelsThenField, """["p"]"""));
    }

    // --- Integration -----------------------------------------------------------------------------------

    [Fact]
    public async Task Ado_work_items_are_classified_end_to_end()
    {
        const string Batch = """
        {"value":[{"id":7,"rev":3,"fields":{"System.WorkItemType":"Bug","System.State":"Active","System.Title":"t",
          "System.ChangedDate":"2026-01-01T00:00:00Z","System.CreatedDate":"2026-01-01T00:00:00Z",
          "System.AssignedTo":{"displayName":"Ana"},"Custom.FoundInEnvironment":"Staging"},"relations":[]}]}
        """;
        var handler = new FakeAdoHandler().On(HttpMethod.Post, "/_apis/wit/workitemsbatch", _ => FakeAdoHandler.Ok(Batch));

        var item = Assert.Single(await new AdoWorkItemSource(DemoAdo.Client(handler), Classifier).GetWorkItemsAsync("Payments", [7])).Record!;

        Assert.Equal(("UAT", "verified", "Staging", "field:Custom.FoundInEnvironment", "matched"),
            (item.Environment, item.EnvironmentStatus, item.EnvironmentRaw, item.EnvironmentSource, item.EnvironmentReason));
        Assert.Single(item.EnvironmentFindings);
    }

    [Fact]
    public void Settings_report_rule_problems_as_warnings_and_never_stop_ingestion()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ADO_ORG_URL"] = "https://dev.azure.com/contoso",
            ["ADO_PROJECTS"] = "Payments",
            ["ADO_READ_PAT"] = "test-only-not-a-token",
            ["CHECKPOINT_STORE"] = "file",
            ["ADO_ENVIRONMENT_RULES"] = """{"providers":{"azure_devops":{"projects":{"Payments":{"sources":[],"values":{}}}}}}""",
        };

        var settings = IngestorSettings.FromValues(values);

        Assert.Single(settings.EnvironmentWarnings);
        Assert.Equal("rules_invalid", settings.Environments.Classify(
            new DefectEvidence("azure_devops", "Payments", null, null, new Dictionary<string, string>(), [], [])).Reason);
    }

    [Fact]
    public void The_example_rules_file_in_the_repository_is_valid()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VisionIQ.sln")))
        {
            directory = directory.Parent;
        }

        var example = File.ReadAllText(Path.Combine(directory!.FullName, "config", "environment-rules.example.json"));
        var classifier = DefectEnvironmentClassifier.FromJson(example, new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["azure_devops"] = ["Payments", "Ticketing", "Mobile"],
            ["jira"] = ["CORE", "WEB"],
        });

        Assert.Empty(classifier.Problems);
        Assert.Equal("PROD", classifier.Classify(new DefectEvidence("jira", "CORE", null, null, new Dictionary<string, string>(), ["production"], [])).Normalized);
    }
}
