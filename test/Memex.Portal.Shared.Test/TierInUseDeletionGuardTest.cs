using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 A plan TIER node cannot be deleted while a registered instance stands on it (#5894).
///
/// <para>The ladder is data: <c>Admin/Tiers/{id}</c> is what makes a plan id known. On the public
/// registry <c>Admin/Tiers/sme</c> was deleted under a dedicated client instance storing <c>sme</c>;
/// its plan became unknown, decided at the baseline, and it was refused 18 packages as "free". The
/// guard refuses that delete and names the instances; an unused tier, and a tier node carrying a
/// RETIRED id (resolved to its successor everywhere), stay deletable.</para>
/// </summary>
public class TierInUseDeletionGuardTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TierNodeType = "Store/Tier";
    private const string Owner = "tier-owner";

    /// <summary>The tier node's content as the registry reads it (rank, all-access) — the Store's
    /// <c>TierContent</c> shape, test-local because this fixture does not run the Store.</summary>
    public record TierNode
    {
        public int Rank { get; init; }
        public bool AllAccess { get; init; }
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .AddMeshNodes(new MeshNode(TierNodeType)
            {
                Name = "Tier",
                IsSatelliteType = false,
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<TierNode>()),
            })
            .ConfigureHub(config => config.WithType<TierNode>(nameof(TierNode)));

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private static string TierPath(string id) => $"{PlanTierLadder.Namespace}/{id}";

    private Task SeedTier(string id, int rank, bool allAccess, CancellationToken ct) =>
        Access.RunAsSystem(() => NodeFactory.CreateOrUpdateNode(new MeshNode(id, PlanTierLadder.Namespace)
            {
                Name = id,
                NodeType = TierNodeType,
                State = MeshNodeState.Active,
                Content = new TierNode { Rank = rank, AllAccess = allAccess },
            }))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    private Task Register(string instanceId, string tier, CancellationToken ct) =>
        new MeshWeaverInstanceService(
                Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
                Mesh,
                Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
                new ConfigurationBuilder().Build())
            .Register(Owner, "Owner", "owner@test.com", instanceId, instanceId, tier: tier)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    /// <summary>The delete's fault, or null when it went through. Run as System: the guard does not
    /// exempt System, and System is what removes the RLS question from the measurement.</summary>
    private Task<Exception?> TryDelete(string path, CancellationToken ct) =>
        Access.RunAsSystem(() => NodeFactory.DeleteNode(path))
            .Select(_ => (Exception?)null)
            .Catch((Exception ex) => Observable.Return<Exception?>(ex))
            .DefaultIfEmpty(null)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    private Task<MeshNode?> Read(string path, CancellationToken ct) =>
        Access.RunAsSystem(() => Mesh.GetMeshNode(path, TimeSpan.FromSeconds(10)).Take(1))
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    [Fact(Timeout = 300_000)]
    public async Task ATierAnInstanceStandsOn_CannotBeDeleted_AndAnUnusedOneCan()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTier("free", 0, false, ct);
        await SeedTier("pro", 20, false, ct);
        await SeedTier("personal", 10, false, ct);
        await Register("pro-client", "pro", ct);

        var refused = await TryDelete(TierPath("pro"), ct);
        refused.Should().NotBeNull("an instance stands on 'pro'; deleting its tier node would make its plan unknown");
        refused?.Message.Should().Contain("pro-client", "the refusal names the instance that has to be moved first");
        (await Read(TierPath("pro"), ct)).Should().NotBeNull("the refused delete must leave the tier node in place");

        // The control: nothing stands on `personal`, so the guard must not refuse everything.
        (await TryDelete(TierPath("personal"), ct)).Should().BeNull("no instance stands on 'personal'");
    }

    /// <summary>What STORAGE holds at <paramref name="path"/> — the authoritative absence check, which
    /// activates no per-node hub at an address that has no node.</summary>
    private Task<MeshNode?> Stored(string path, CancellationToken ct) =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, Mesh.JsonSerializerOptions)
            .DefaultIfEmpty(null)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    /// <summary>The move's response, issued as System for the same reason <see cref="TryDelete"/> runs
    /// as System: the guard does not exempt it, and it takes RLS out of the measurement.</summary>
    private Task<MoveNodeResponse> Move(string source, string target, CancellationToken ct) =>
        ObserveNodeOperation(new MoveNodeRequest(source, target), o => o.WithAccessContext(new AccessContext
            {
                ObjectId = WellKnownUsers.System,
                Name = WellKnownUsers.System,
            }))
            .Select(d => d.Message)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    /// <summary>
    /// 🚨 A MOVE removes the tier from where the ladder reads it exactly as a delete does, so it is
    /// refused for the same reason. Before the move ran the source's delete pre-flight, this move
    /// SUCCEEDED and the in-use tier left the ladder unguarded. The second move is the negative
    /// control: an unused tier moves freely, so the refusal is the guard's and not a broken move.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task MovingATierAnInstanceStandsOn_IsRefused_AndMovingAnUnusedOneIsNot()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTier("free", 0, false, ct);
        await SeedTier("pro", 20, false, ct);
        await SeedTier("personal", 10, false, ct);
        await Register("pro-client", "pro", ct);
        const string archive = "Admin/TierArchive";

        var refused = await Move(TierPath("pro"), $"{archive}/pro", ct);
        Output.WriteLine($"move pro: success={refused.Success} reason={refused.RejectionReason} error={refused.Error}");
        refused.Success.Should().BeFalse("moving the tier out of the ladder makes the instance's plan unknown, exactly like deleting it");
        refused.RejectionReason.Should().Be(NodeMoveRejectionReason.ValidationFailed);
        refused.Error.Should().Contain("pro-client", "the refusal carries the guard's own reason, naming the instance");
        (await Read(TierPath("pro"), ct)).Should().NotBeNull("the refused move must leave the tier where it was");
        (await Stored($"{archive}/pro", ct)).Should().BeNull("the refusal comes before the copy creates anything");

        var moved = await Move(TierPath("personal"), $"{archive}/personal", ct);
        Output.WriteLine($"move personal: success={moved.Success} reason={moved.RejectionReason} error={moved.Error}");
        moved.Success.Should().BeTrue(moved.Error ?? "nothing stands on 'personal', so the guard has no objection");
        (await Read($"{archive}/personal", ct)).Should().NotBeNull("a legitimate move lands at the target");
        (await Stored(TierPath("personal"), ct)).Should().BeNull("and leaves nothing at the source");
    }

    [Fact(Timeout = 300_000)]
    public async Task TheBaselineTier_IsInUseByAnInstanceWithNoPaidPlan()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTier("free", 0, false, ct);
        await Register("free-client", "free", ct);

        var refused = await TryDelete(TierPath("free"), ct);
        refused.Should().NotBeNull("an instance stands on the baseline plan");
        refused?.Message.Should().Contain("free-client");
    }

    [Fact(Timeout = 300_000)]
    public async Task ATierNodeCarryingTheRetiredSmeId_StaysDeletable_WhileItsSuccessorIsGuarded()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTier("dedicated", 25, true, ct);
        await SeedTier("sme", 25, true, ct);
        // Registered on `sme`: the record stores the successor, the guard counts it on `dedicated`.
        await Register("dedicated-client", "sme", ct);

        (await TryDelete(TierPath("sme"), ct)).Should().BeNull(
            "'sme' is retired — it resolves to 'dedicated', so nothing stands on its own node");
        var refused = await TryDelete(TierPath("dedicated"), ct);
        refused.Should().NotBeNull("the instance registered on 'sme' stands on its successor 'dedicated'");
        refused?.Message.Should().Contain("dedicated-client");
    }
}
