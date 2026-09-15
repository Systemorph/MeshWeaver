using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A grant is visible to the VERY NEXT permission check</b> — the read-after-write invariant
/// of the permission fold, isolated.
///
/// <para>#4061 finding 2 reports an intermittent denial on a partition whose creator grant had been
/// written durably and returned before the denied create began. The fold reads grants through a
/// process-wide cached, synced QUERY (<c>PermissionEvaluator.ObserveEffectiveAssignments</c> →
/// <c>SecurityQuery</c> → <c>IMeshNodeStreamCache.GetQuery</c>), so a decision taken from a snapshot
/// older than the write that authorised it denies a right the store already grants. This pins the
/// property directly: open the partition's grant query, write a NEW grant, and require the very next
/// check to see it.</para>
///
/// <para><b>Measured: it holds on an in-memory mesh, on `main`, every run.</b> That is a NEGATIVE
/// result for the staleness hypothesis at this level and it is recorded on the issue — the
/// PostgreSQL failure needs something this mesh does not have. It stays as a guard because the
/// invariant is real and its loss would be silent: a permission fold that stops being
/// read-after-write consistent denies rights that exist, which reads as a policy decision.</para>
/// </summary>
public class AFreshGrantIsVisibleToTheNextCheckTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string SpaceId = "grantprobe";
    private const string Newcomer = "newcomer-user";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => Bootstrap.Bootstrap(builder)
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType()
            .ConfigureHub(c => c
                .WithQuiesceTimeout(TestQuiesceTimeout)
                .WithRequestTimeout(TimeSpan.FromSeconds(60)));

    [Fact(Timeout = 180_000)]
    public async Task AFreshGrantIsVisibleToTheVeryNextPermissionCheck()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        await meshService.CreateNode(new MeshNode(SpaceId)
        {
            Name = "Grant Probe", NodeType = SpaceNodeType.NodeType,
            State = MeshNodeState.Active, Content = new Space(),
        }).Should().Within(TestTimeouts.CrossSilo).Emit("the creator may create a Space", cancellationToken: TestContext.Current.CancellationToken);

        // Opens $security-access:{SpaceId} and settles its snapshot — the newcomer has nothing.
        var before = await Mesh.GetEffectivePermissions(SpaceId, Newcomer)
            .Take(1).Timeout(TestTimeouts.CrossSilo).FirstAsync();
        Output.WriteLine($"before grant: {before}");
        before.HasFlag(Permission.Create).Should().BeFalse(
            "the newcomer holds no grant yet — if this were already Create the probe below would "
            + "be measuring nothing");

        // Now grant, durably, as System — exactly what SpacePostCreationHandler does.
        using (access.ImpersonateAsSystem())
        {
            await meshService.CreateNode(new MeshNode($"{Newcomer}_Access", $"{SpaceId}/_Access")
            {
                NodeType = "AccessAssignment",
                Name = $"{Newcomer} Access",
                MainNode = SpaceId,
                Content = new AccessAssignment
                {
                    AccessObject = Newcomer,
                    DisplayName = Newcomer,
                    Roles = [new RoleAssignment { Role = Role.Admin.Id, Denied = false }],
                },
            }).Should().Within(TestTimeouts.CrossSilo).Emit("the grant write must complete", cancellationToken: TestContext.Current.CancellationToken);
        }

        // THE PROPERTY: the very next check must see it. The grant's durable write has returned.
        var after = await Mesh.GetEffectivePermissions(SpaceId, Newcomer)
            .Take(1).Timeout(TestTimeouts.CrossSilo).FirstAsync();
        Output.WriteLine($"after grant (first emission): {after}");
        after.HasFlag(Permission.Create).Should().BeTrue(
            "the grant is DURABLY written and its create returned before this read started — a "
            + "permission decision taken from a snapshot older than the write that authorised it "
            + "denies a right the store already grants");
    }
}
