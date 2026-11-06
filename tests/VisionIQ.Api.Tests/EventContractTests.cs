using System.Text.Json;
using Json.Schema;
using VisionIQ.Api.Features.Events;

namespace VisionIQ.Api.Tests;

/// <summary>
/// S03-06 cross-language parity: the same golden fixtures as the Python tests, and .NET output checked against the
/// shared JSON Schemas themselves, so the two services cannot drift apart silently.
/// </summary>
public sealed class EventContractTests
{
    internal static readonly string ContractsDirectory = FindContracts();

    private static readonly Lazy<Dictionary<string, JsonSchema>> Schemas = new(LoadSchemas);

    public static TheoryData<string> ValidFixtures() => Fixtures("valid");

    public static TheoryData<string> InvalidFixtures() => Fixtures("invalid");

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public void Valid_fixtures_parse_and_round_trip_through_the_schema(string name)
    {
        var envelope = EventEnvelope.Parse(File.ReadAllBytes(FixturePath("valid", name)));

        AssertMatchesSchema(envelope);
    }

    [Theory]
    [MemberData(nameof(InvalidFixtures))]
    public void Invalid_fixtures_are_rejected_like_the_schema_rejects_them(string name)
    {
        var bytes = File.ReadAllBytes(FixturePath("invalid", name));

        Assert.False(Evaluate(bytes).IsValid, $"{name} should violate the JSON Schema");
        Assert.Throws<EventContractException>(() => EventEnvelope.Parse(bytes));
    }

    [Fact]
    public void Produced_analysis_requested_matches_the_shared_schema()
    {
        var runId = Guid.NewGuid();
        var envelope = EventEnvelope.Create(
            EventContracts.AnalysisRequested, "visioniq-dev", runId.ToString(), "4bf92f3577b34da6a3ce929d0e0e4736", runId,
            "12", new AnalysisRequestedV1("VisionIQ-Demo", "4711", 12, "demo-repo", new string('a', 40), AnalysisMode.Deep, "project:VisionIQ-Demo"));

        EventContracts.Validate(envelope);
        AssertMatchesSchema(envelope);
        Assert.Contains("\"analysis_mode\":\"deep\"", System.Text.Encoding.UTF8.GetString(envelope.ToUtf8Json()), StringComparison.Ordinal);
    }

    [Fact]
    public void Analysis_completed_states_serialize_in_contract_casing()
    {
        var envelope = EventEnvelope.Parse(File.ReadAllBytes(FixturePath("valid", "analysis.completed.v1.json")));

        var data = envelope.ReadData<AnalysisCompletedV1>();

        Assert.Equal(ResultState.Partial, data.ResultState);
        Assert.Equal("\"PARTIAL\"", JsonSerializer.Serialize(data.ResultState, EventEnvelope.Json));
    }

    [Fact]
    public void Rejection_messages_never_echo_payload_values()
    {
        var json = File.ReadAllText(FixturePath("valid", "analysis.requested.v1.json"))
            .Replace("\"4711\"", "\"secret ticket title\"", StringComparison.Ordinal);

        var error = Assert.Throws<EventContractException>(() => EventEnvelope.Parse(System.Text.Encoding.UTF8.GetBytes(json)));

        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Topics_match_the_shared_topology()
    {
        using var topology = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractsDirectory, "topology.json")));
        var shared = topology.RootElement.GetProperty("events").EnumerateArray()
            .Select(e => $"{e.GetProperty("event_type").GetString()} v{e.GetProperty("schema_version").GetInt32()} -> {e.GetProperty("topic").GetString()}")
            .Order();
        var dotnet = EventTopology.Topics.Select(p => $"{p.Key.Type} v{p.Key.Version} -> {p.Value}").Order();

        Assert.Equal(shared, dotnet);
        Assert.Equal(EventTopology.PushPath, topology.RootElement.GetProperty("push_path").GetString());
    }

    internal static string FixturePath(string kind, string name) => Path.Combine(ContractsDirectory, "fixtures", kind, name);

    private static void AssertMatchesSchema(EventEnvelope envelope)
    {
        var results = Evaluate(envelope.ToUtf8Json());
        Assert.True(results.IsValid, JsonSerializer.Serialize(results));
    }

    private static EvaluationResults Evaluate(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var key = $"{document.RootElement.GetProperty("event_type").GetString()}.v{document.RootElement.GetProperty("schema_version").GetInt32()}";
        // An unknown version has no schema, which is itself a rejection; evaluate it against v1 to prove that too.
        var schema = Schemas.Value.TryGetValue(key, out var match) ? match : Schemas.Value[key[..key.LastIndexOf(".v", StringComparison.Ordinal)] + ".v1"];
        return schema.Evaluate(document.RootElement.Clone(), new EvaluationOptions { OutputFormat = OutputFormat.List });
    }

    private static Dictionary<string, JsonSchema> LoadSchemas()
    {
        var options = new BuildOptions();
        var files = Directory.GetFiles(ContractsDirectory, "*.schema.json").OrderBy(f => !Path.GetFileName(f).StartsWith("envelope.", StringComparison.Ordinal));
        var schemas = new Dictionary<string, JsonSchema>();
        foreach (var file in files)
        {
            var schema = JsonSchema.FromFile(file, options);
            options.SchemaRegistry.Register(schema);
            schemas[Path.GetFileName(file).Replace(".schema.json", string.Empty, StringComparison.Ordinal)] = schema;
        }

        return schemas;
    }

    private static TheoryData<string> Fixtures(string kind)
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(ContractsDirectory, "fixtures", kind), "*.json").Order())
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    private static string FindContracts()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "contracts", "events");
            if (File.Exists(Path.Combine(candidate, "envelope.v1.schema.json")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("contracts/events not found above the test output directory.");
    }
}
