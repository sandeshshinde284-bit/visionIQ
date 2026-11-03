using System.Net;
using VisionIQ.Ingestor.Ado;

namespace VisionIQ.Ingestor.Tests;

/// <summary>B-6: live project membership read from the project's teams.</summary>
public sealed class AdoProjectMembershipTests
{
    private const string Teams = """{"value":[{"id":"team-1","name":"Payments"},{"id":"team-2","name":"Core"}],"count":2}""";
    private const string Team1 = """{"value":[{"identity":{"id":"guid-ana","uniqueName":"ana@contoso.com"}}],"count":1}""";
    private const string Team2 = """{"value":[{"identity":{"id":"guid-raj","uniqueName":"raj@contoso.com"}}],"count":1}""";

    private static FakeAdoHandler Server() => new FakeAdoHandler()
        .On(HttpMethod.Get, "/teams/team-1/members", _ => FakeAdoHandler.Ok(Team1))
        .On(HttpMethod.Get, "/teams/team-2/members", _ => FakeAdoHandler.Ok(Team2))
        .On(HttpMethod.Get, "/teams", _ => FakeAdoHandler.Ok(Teams));

    [Fact]
    public async Task Members_of_any_team_are_project_members_by_email_or_identity_id()
    {
        var membership = new AdoTeamProjectMembership(DemoAdo.Client(Server()));

        Assert.True(await membership.IsMemberAsync("VisionIQ Demo", "ANA@contoso.com"));
        Assert.True(await membership.IsMemberAsync("VisionIQ Demo", "guid-raj"));
        Assert.False(await membership.IsMemberAsync("VisionIQ Demo", "eve@contoso.com"));
        Assert.False(await membership.IsMemberAsync("VisionIQ Demo", " "));
    }

    [Fact]
    public async Task Membership_is_cached_briefly()
    {
        var handler = Server();
        var membership = new AdoTeamProjectMembership(DemoAdo.Client(handler));

        await membership.IsMemberAsync("VisionIQ Demo", "ana@contoso.com");
        var calls = handler.Requests.Count;
        await membership.IsMemberAsync("VisionIQ Demo", "raj@contoso.com");

        Assert.Equal(calls, handler.Requests.Count);
    }

    [Fact]
    public async Task An_unreadable_membership_is_an_error_not_a_grant()
    {
        var handler = new FakeAdoHandler().On(HttpMethod.Get, "/teams", _ => FakeAdoHandler.Status(HttpStatusCode.Forbidden));
        var membership = new AdoTeamProjectMembership(DemoAdo.Client(handler));

        await Assert.ThrowsAsync<AdoSourceException>(() => membership.IsMemberAsync("VisionIQ Demo", "ana@contoso.com"));
    }
}
