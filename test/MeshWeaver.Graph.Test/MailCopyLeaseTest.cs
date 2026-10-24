using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.MailCopy;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The copy's lease is a conditional commit at the owning node: of N syncs that try to take a free lease at
/// once, exactly one is told it holds it. A lease that two syncs can both take is two syncs writing one copy.
/// </summary>
public class MailCopyLeaseTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder).AddMailCopyTypes();

    [Fact(Timeout = 180_000)]
    public async Task OfManySyncsTakingAFreeLeaseAtOnce_ExactlyOneWins()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = $"user{Guid.NewGuid():N}"[..12];
        await SeedTopLevel(MeshNode.FromPath(owner) with
        {
            Name = "Jane Roe",
            NodeType = UserNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new User { Email = "jane@acme.example", FullName = "Jane Roe" },
        }).WaitAsync(ct);
        var store = new MeshMailCopyStore(() => Mesh);
        await store.Create(owner, new MailCopySource { Provider = "outlook", Generation = "g1" }).Timeout(Bound).Await(ct);

        var contenders = Enumerable.Range(0, 8).Select(i => $"sync-{i}").ToList();
        var results = await Observable.Merge(contenders.Select(who =>
                store.Commit(owner, "outlook", "g1", current => current.LeaseHolder is null || current.LeaseUntil is not { } until || until <= DateTimeOffset.UtcNow
                        ? current with { LeaseHolder = who, LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(10) }
                        : null)
                    .Select(committed => (Who: who, Won: committed?.LeaseHolder == who))))
            .ToList().Timeout(Bound).Await(ct);

        results.Count(r => r.Won).Should().Be(1, "who won: " + string.Join(",", results.Where(r => r.Won).Select(r => r.Who)));
        var holder = (await store.Source(owner, "outlook").Timeout(Bound).Await(ct))!.LeaseHolder;
        results.Single(r => r.Won).Who.Should().Be(holder, "the stored holder is the one that was told it won");
    }
}
