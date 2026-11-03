using VisionIQ.Api.Features.Evidence;

namespace VisionIQ.Api.Tests;

/// <summary>LRN-02/03 permission revocation and no-data-leak regression tests.</summary>
public sealed class EvidenceQueryServiceTests
{
    private static readonly string ItemKey = new('a', 64);

    /// <summary>Returns a minimal redacted revision with an explicit stored ACL.</summary>
    private static EvidenceReadCandidate Candidate(bool verified = true,
        IReadOnlyList<string>? principals = null) =>
        new(new EvidenceIdentity("confluence", "https://example.atlassian.net/wiki",
                "ENG", null, "design_page", "123"),
            "7", "https://example.atlassian.net/wiki/pages/123",
            """{"kind":"design_page","version":7}""",
            verified, principals ?? ["group-architecture"]);

    [Fact]
    public async Task Unverified_acl_never_reaches_a_provider_or_a_requester()
    {
        var verifier = new StubVerifier { Allowed = true };
        var query = new EvidenceQueryService(new StubReader(Candidate(false)), verifier);
        var result = await query.FindLatestAuthorizedAsync(ItemKey,
            new EvidenceRequester("employee", new HashSet<string> { "group-architecture" }));
        Assert.Null(result);
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public async Task Unauthorized_group_is_denied_without_provider_callback()
    {
        var verifier = new StubVerifier { Allowed = true };
        var query = new EvidenceQueryService(new StubReader(Candidate()), verifier);
        var result = await query.FindLatestAuthorizedAsync(ItemKey,
            new EvidenceRequester("employee", new HashSet<string> { "group-other" }));
        Assert.Null(result);
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public async Task Current_provider_revocation_blocks_old_approved_snapshot()
    {
        var verifier = new StubVerifier { Allowed = false };
        var query = new EvidenceQueryService(new StubReader(Candidate()), verifier);
        var result = await query.FindLatestAuthorizedAsync(ItemKey,
            new EvidenceRequester("employee", new HashSet<string> { "group-architecture" }));
        Assert.Null(result);
        Assert.Equal(1, verifier.Calls);
    }

    [Fact]
    public async Task Authorized_result_contains_only_redacted_metadata_not_acl_principals()
    {
        var verifier = new StubVerifier { Allowed = true };
        var query = new EvidenceQueryService(new StubReader(Candidate()), verifier);
        var result = await query.FindLatestAuthorizedAsync(ItemKey,
            new EvidenceRequester("employee", new HashSet<string> { "group-architecture" }));
        Assert.NotNull(result);
        Assert.Equal("7", result.ProviderRevisionId);
        Assert.DoesNotContain("group-architecture", result.RedactedJson, StringComparison.Ordinal);
        Assert.Equal(1, verifier.Calls);
    }

    [Fact]
    public async Task Secure_default_provider_verifier_denies_even_if_acl_is_verified()
    {
        var query = new EvidenceQueryService(
            new StubReader(Candidate()), new DenyUnknownSourcePermissionVerifier());
        Assert.Null(await query.FindLatestAuthorizedAsync(ItemKey,
            new EvidenceRequester("employee", new HashSet<string> { "group-architecture" })));
    }

    [Fact]
    public async Task Invalid_canonical_keys_are_rejected_without_database_access()
    {
        var query = new EvidenceQueryService(
            new StubReader(Candidate()), new DenyUnknownSourcePermissionVerifier());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            query.FindLatestAuthorizedAsync("not-a-sha", new EvidenceRequester(
                "user", new HashSet<string>())));
    }

    /// <summary>Returns a fixed candidate without depending on a database.</summary>
    private sealed class StubReader(EvidenceReadCandidate? candidate) : ICanonicalEvidenceReader
    {
        /// <summary>Reads the fixture's latest revision.</summary>
        public Task<EvidenceReadCandidate?> FindLatestAsync(
            string itemKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(candidate);
    }

    /// <summary>Simulates current Confluence/ADO access becoming granted or revoked.</summary>
    private sealed class StubVerifier : IProviderAccessVerifier
    {
        public bool Allowed { get; init; }
        public int Calls { get; private set; }

        /// <summary>Records that the caller's current source access was checked.</summary>
        public Task<bool> CanReadNowAsync(EvidenceIdentity identity,
            EvidenceRequester requester, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Allowed);
        }
    }
}
