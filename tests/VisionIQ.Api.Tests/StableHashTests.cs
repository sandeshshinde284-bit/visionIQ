using VisionIQ.Api.Features.Graph;
using VisionIQ.Api.Features.TicketFixes;

namespace VisionIQ.Api.Tests;

/// <summary>
/// N-18: these hashes are stored keys, so their bytes must never change. Expected values were computed
/// independently by the Python implementation (length-prefixed UTF-8, little-endian int32, -1 for null).
/// </summary>
public sealed class StableHashTests
{
    [Fact]
    public void Graph_hash_matches_the_independent_reference() =>
        Assert.Equal("5c480c12685dc59f97cf1e2be59e7599901b27d3ae91959578cce93b6779ebbe", GraphHashes.Hash("node", "Commit", "abc"));

    [Fact]
    public void Ticket_fix_association_key_matches_the_independent_reference() =>
        Assert.Equal("0a1d7470f06ab97cc4070f6964df7f46e09f650e5f0c15bdaf48a5146d091b4b",
            new TicketFixClaim("https://dev.azure.com/contoso", "Payments", "1234", "repo-1", null, new string('a', 40),
                TicketFixSourceClassification.Explicit, ["evidence:" + new string('b', 64) + ":1"], []).AssociationKey());
}
