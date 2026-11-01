using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using VisionIQ.Api.Features.Evidence;
using VisionIQ.Api.Logging;
using VisionIQ.Ingestor.Ado;

namespace VisionIQ.Api.Tests;

/// <summary>Collects formatted log lines (with level and scopes) for assertions.</summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<object> scopes = [];

    public List<(LogLevel Level, string Message, string Scopes)> Lines { get; } = [];

    public string All => string.Join("\n", Lines.Select(l => $"{l.Level} {l.Message} {l.Scopes}"));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        scopes.Add(state);
        return new Pop(scopes);
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add((logLevel, formatter(state, exception), string.Join(";", scopes.Select(s => s is IEnumerable<KeyValuePair<string, object>> d ? string.Join(",", d.Select(p => p.Key + "=" + p.Value)) : s.ToString()))));

    private sealed class Pop(List<object> scopes) : IDisposable
    {
        public void Dispose() => scopes.RemoveAt(scopes.Count - 1);
    }
}

public sealed class LoggingTests
{
    private const string Key = "a-test-only-pseudonym-key-of-32-plus-characters";

    [Fact]
    public void Pseudonyms_are_stable_case_insensitive_keyed_and_never_contain_the_email()
    {
        var keyed = new IdentityPseudonymizer(Key);
        var pseudonym = keyed.For("Ana.Lopez@Contoso.com");

        Assert.True(keyed.IsKeyed);
        Assert.Matches("^u-[0-9a-f]{16}$", pseudonym);
        Assert.Equal(pseudonym, keyed.For("ana.lopez@contoso.com "));
        Assert.DoesNotContain("ana", pseudonym, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(pseudonym, new IdentityPseudonymizer(null).For("ana.lopez@contoso.com")); // the key matters
        Assert.NotEqual(pseudonym, keyed.For("eve@contoso.com"));
        Assert.Equal("u-anonymous", keyed.For(" "));
        Assert.False(new IdentityPseudonymizer("too-short").IsKeyed);
    }

    private static async Task<(DefaultHttpContext Context, CapturingLogger<RequestLoggingMiddleware> Log)> Run(string? incomingId)
    {
        var log = new CapturingLogger<RequestLoggingMiddleware>();
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/v1/graph/work-items/Payments/1";
        context.Request.QueryString = new QueryString("?hops=2&token=QUERY-SECRET-123");
        context.Request.Headers.Authorization = "Bearer HEADER-SECRET-456";
        if (incomingId is not null)
        {
            context.Request.Headers[RequestLoggingMiddleware.Header] = incomingId;
        }

        await new RequestLoggingMiddleware(http => { http.Response.StatusCode = 404; return Task.CompletedTask; }, log).InvokeAsync(context);
        return (context, log);
    }

    [Fact]
    public async Task Each_request_gets_one_line_with_a_request_id_and_no_secrets()
    {
        var (context, log) = await Run(null);

        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("GET /api/v1/graph/work-items/Payments/1 responded 404", line.Message, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{32}$", context.TraceIdentifier);
        Assert.Contains("requestId=" + context.TraceIdentifier, line.Scopes, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_well_formed_incoming_request_id_is_kept_and_a_malicious_one_replaced()
    {
        Assert.Equal("impact-agent-req-0001", (await Run("impact-agent-req-0001")).Context.TraceIdentifier);

        var (context, log) = await Run("x\nFAKE LOG LINE level=Error");
        Assert.Matches("^[0-9a-f]{32}$", context.TraceIdentifier);
        Assert.DoesNotContain("FAKE LOG LINE", log.All, StringComparison.Ordinal);
    }

    private sealed class Reader(EvidenceReadCandidate? candidate) : ICanonicalEvidenceReader
    {
        public Task<EvidenceReadCandidate?> FindLatestAsync(string itemKey, CancellationToken cancellationToken = default) => Task.FromResult(candidate);
    }

    private sealed class Verifier(bool allow) : IProviderAccessVerifier
    {
        public Task<bool> CanReadNowAsync(EvidenceIdentity identity, EvidenceRequester requester, CancellationToken cancellationToken = default) => Task.FromResult(allow);
    }

    private static readonly EvidenceIdentity Identity = new("azure_devops", "https://dev.azure.com/contoso", "Payments", null, "work_item_current", "1");
    private static readonly EvidenceRequester Ana = new("ana@contoso.com", new HashSet<string> { "scope-a" });

    [Theory]
    [InlineData(false, false, "x", true, "not stored or deleted in source")]
    [InlineData(true, false, "scope-a", true, "access list not verified")]
    [InlineData(true, true, "scope-b", true, "requester holds none of the item's access scopes")]
    [InlineData(true, true, "scope-a", false, "source denied current access")]
    public async Task Every_hidden_item_logs_why(bool exists, bool verified, string scope, bool sourceAllows, string reason)
    {
        var log = new CapturingLogger<EvidenceQueryService>();
        var candidate = exists ? new EvidenceReadCandidate(Identity, "1", "https://x", "{}", verified, [scope]) : null;

        Assert.Null(await new EvidenceQueryService(new Reader(candidate), new Verifier(sourceAllows), log).FindLatestAuthorizedAsync(new string('a', 64), Ana));

        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Debug, line.Level);
        Assert.Contains(reason, line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ana@contoso.com", log.All, StringComparison.Ordinal);
    }

    private sealed class Membership(bool fail) : IAdoProjectMembership
    {
        public Task<bool> IsMemberAsync(string project, string principalId, CancellationToken cancellationToken = default) =>
            fail ? throw new AdoSourceException(null, "ADO is down; PAT=SHOULD-NOT-APPEAR") : Task.FromResult(false);
    }

    [Fact]
    public async Task A_non_member_is_a_debug_reason_and_an_outage_is_a_warning_without_details()
    {
        var log = new CapturingLogger<AdoProjectAccessVerifier>();
        await new AdoProjectAccessVerifier(new Membership(fail: false), "https://dev.azure.com/contoso", ["Payments"], log).CanReadNowAsync(Identity, Ana);
        await new AdoProjectAccessVerifier(new Membership(fail: true), "https://dev.azure.com/contoso", ["Payments"], log).CanReadNowAsync(Identity, Ana);

        Assert.Equal(LogLevel.Debug, log.Lines[0].Level);
        Assert.Contains("not a current member of project Payments", log.Lines[0].Message, StringComparison.Ordinal);
        Assert.Equal(LogLevel.Warning, log.Lines[1].Level);
        Assert.Contains("AdoSourceException", log.Lines[1].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SHOULD-NOT-APPEAR", log.All, StringComparison.Ordinal); // exception messages are never logged
    }
}
