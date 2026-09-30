using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
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
/// 🚨 A global administrator sets the plan of an instance ANOTHER user registered — and only a
/// global administrator can.
///
/// <para>An instance record lives in its registrant's partition (<c>{owner}/MeshWeaverInstance/{id}</c>),
/// and a global administrator is a PLATFORM admin, not a data superuser: it holds no read grant on
/// another user's partition. <c>InstancePlanService.SetPlan</c> read and wrote the record under the
/// CALLER's identity, so the Settings ▸ Instance grants Plan form answered "Access denied: user … lacks
/// Read permission on '{owner}/MeshWeaverInstance/{id}'" for every instance its admin had not
/// registered (measured on the public registry, 2026-09-30). <c>RevokeKey</c> already handles the same
/// situation: it checks <c>hub.IsGlobalAdmin()</c> and then writes as System.</para>
///
/// <para>This suite stands on <see cref="MonolithMeshTestBase.ConfigureMeshBase"/> WITHOUT the default
/// public admin grant, and the platform admin's only grant is <c>Admin</c> on the Admin partition — the
/// production shape. Under the default grant everyone may read everything, which is why the existing
/// plan test never saw the defect.</para>
/// </summary>
public class InstancePlanSetByGlobalAdminTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string AdminPartition = "Admin";
    private const string PlatformAdmin = "platform-boss";
    private const string Stranger = "plain-member";
    private const string Owner = "instance-owner";
    private const string InstanceId = "owned-elsewhere";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(AdminPartition) { Name = "Admin", NodeType = "Markdown" },
                AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", AdminPartition))
            .AddPluginCatalog();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private InstancePlanService Plans => Mesh.ServiceProvider.GetRequiredService<InstancePlanService>();

    /// <summary>Registers the instance under <see cref="Owner"/>'s partition and returns its path.</summary>
    private async Task<string> RegisterElsewhere(CancellationToken ct)
    {
        var service = new MeshWeaverInstanceService(
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
            new ConfigurationBuilder().Build());
        var registered = await service.Register(Owner, "Owner", "owner@test.com", InstanceId, InstanceId)
            .Timeout(TimeSpan.FromSeconds(60)).Await(ct);
        return registered.Node.Path;
    }

    /// <summary>The plan on the record, read as System (nobody else may read the owner's partition).</summary>
    private Task<string?> PlanOf(string path, CancellationToken ct) =>
        Access.RunAsSystem(() => Mesh.GetMeshNode(path, TimeSpan.FromSeconds(10)).Take(1))
            .Select(node => node?.ContentAs<MeshWeaverInstance>(Mesh.JsonSerializerOptions)?.Plan)
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    private Task<MeshNode> SetPlanAs(string userId, string path, string plan, CancellationToken ct)
    {
        using (Access.SwitchAccessContext(new AccessContext { ObjectId = userId, Name = userId }))
            return Plans.SetPlan(path, plan)
                .Timeout(TimeSpan.FromSeconds(60))
                .Await(ct);
    }

    /// <summary>
    /// THE FORM'S CASE: a global administrator promotes an instance another user registered. Unfixed,
    /// the read of the record is refused ("lacks Read permission") and the plan is unchanged.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AGlobalAdmin_SetsThePlanOfAnInstanceAnotherUserRegistered()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = await RegisterElsewhere(ct);
        path.Should().StartWith(Owner + "/", "the record lives in its registrant's partition — the premise of the defect");

        await SetPlanAs(PlatformAdmin, path, "dedicated", ct);

        (await PlanOf(path, ct)).Should().Be("dedicated",
            "a global administrator's plan change must land on a record in another user's partition");
    }

    /// <summary>
    /// THE CONTROL: a member who is not a global administrator is refused and the plan stays as it
    /// was — the fix must not turn the System write into a way around the admin gate.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ANonAdmin_IsRefused_AndThePlanIsUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = await RegisterElsewhere(ct);
        var before = await PlanOf(path, ct);

        var attempt = () => SetPlanAs(Stranger, path, "dedicated", ct);

        await attempt.Should().ThrowAsync<UnauthorizedAccessException>(
            "setting a plan is a global administrator's act on the registry");
        (await PlanOf(path, ct)).Should().Be(before, "a refused call changes nothing");
    }
}
