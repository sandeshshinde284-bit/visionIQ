using VisionIQ.Api.Features.Evidence;
using VisionIQ.Ingestor.Ado;

namespace VisionIQ.Api.Tests;

/// <summary>N-17: code evidence is readable by live members of the ADO project it was scanned for.</summary>
public sealed class CodeScanAccessTests
{
    private const string Org = "https://dev.azure.com/contoso";

    private sealed class Membership(bool fail = false) : IAdoProjectMembership
    {
        public Task<bool> IsMemberAsync(string project, string principalId, CancellationToken cancellationToken = default) =>
            fail ? throw new AdoSourceException(null, "outage") : Task.FromResult(project == "Payments" && principalId == "ana@contoso.com");
    }

    private static EvidenceIdentity Scan(string project = "Payments", string provider = "git", string kind = "code_scan") =>
        new(provider, "https://dev.azure.com/contoso/Payments/_git/shop", project, "repo-1", kind, "repo-1");

    private static EvidenceRequester Person(string id) =>
        new(id, new HashSet<string> { AdoAccessPrincipals.Project(Org, "Payments") });

    private static readonly CodeScanAccessVerifier Verifier = new(new Membership(), ["Payments", "Mobile"]);

    [Fact]
    public async Task A_live_project_member_can_read_the_projects_code() =>
        Assert.True(await Verifier.CanReadNowAsync(Scan(), Person("ana@contoso.com")));

    [Theory]
    [InlineData("eve@contoso.com", "Payments", "git", "code_scan")]   // not a member
    [InlineData("ana@contoso.com", "Secret", "git", "code_scan")]     // project not approved
    [InlineData("ana@contoso.com", "Payments", "azure_devops", "code_scan")]
    [InlineData("ana@contoso.com", "Payments", "git", "file_blob")]
    public async Task Everything_else_is_denied(string person, string project, string provider, string kind) =>
        Assert.False(await Verifier.CanReadNowAsync(Scan(project, provider, kind), Person(person)));

    [Fact]
    public async Task A_membership_outage_denies() =>
        Assert.False(await new CodeScanAccessVerifier(new Membership(fail: true), ["Payments"]).CanReadNowAsync(Scan(), Person("ana@contoso.com")));

    private sealed class Reader(string principal) : ICanonicalEvidenceReader
    {
        public Task<EvidenceReadCandidate?> FindLatestAsync(string itemKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<EvidenceReadCandidate?>(new(Scan(), "c1", "https://x", "{\"kind\":\"code_scan\"}", true, [principal]));
    }

    [Fact]
    public async Task Code_scanned_for_another_organization_is_denied_even_to_a_project_member()
    {
        var verifier = new ProviderRoutingAccessVerifier(new Dictionary<string, IProviderAccessVerifier> { ["git"] = Verifier });
        var own = new EvidenceQueryService(new Reader(AdoAccessPrincipals.Project(Org, "Payments")), verifier);
        var other = new EvidenceQueryService(new Reader(AdoAccessPrincipals.Project("https://dev.azure.com/other", "Payments")), verifier);

        Assert.NotNull(await own.FindLatestAuthorizedAsync(new string('a', 64), Person("ana@contoso.com")));
        Assert.Null(await other.FindLatestAuthorizedAsync(new string('a', 64), Person("ana@contoso.com")));
    }
}
