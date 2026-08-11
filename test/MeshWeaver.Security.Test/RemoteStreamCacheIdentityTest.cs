using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// SECURITY repro — a remote synchronization stream (the machinery behind EVERY layout-area
/// render, node mirror and remote collection read) must NEVER be shared between two different
/// identities.
///
/// <para><b>The defect.</b> <c>Workspace._remoteStreamCache</c> memoises the stream under
/// <c>(Address, WorkspaceReference)</c> — the identity is NOT part of the key. But the stream's
/// content is identity-SPECIFIC: <c>JsonSynchronizationStream.CreateExternalClient</c> stamps the
/// ambient user onto the <c>SubscribeRequest</c> (<c>Identity = …</c>) and the OWNER applies its
/// RLS gate once, at subscribe time, for exactly that identity. So the FIRST reader of an
/// (address, reference) pair fixes the permission view that every later reader on the same
/// workspace inherits — in both directions:</para>
/// <list type="bullet">
///   <item><b>Disclosure</b> — a permitted reader subscribes first, an unauthorised reader then
///   gets a cache HIT and receives content the owner would have denied them.</item>
///   <item><b>False denial</b> — an unauthorised reader subscribes first and errors; the cached
///   errored stream is then handed to a permitted reader, whose view renders empty.</item>
/// </list>
///
/// <para>Both are asserted below, because both are the SAME bug and a fix that only closes the
/// disclosure direction would leave the render-nothing regression live.</para>
///
/// <para>Reachability is not hypothetical: the Blazor portal hub falls back to a single
/// process-wide <c>portal/anonymous</c> address whenever it is constructed before the request's
/// identity is resolved (<c>UserContextMiddleware</c> resolves <c>PortalApplication</c> on line 58,
/// <c>SetContext</c> only afterwards), so authenticated SSR render passes and anonymous visitors
/// share one workspace — and therefore one of these caches.</para>
/// </summary>
public class RemoteStreamCacheIdentityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly AccessContext Permitted = new() { ObjectId = "alice", Name = "Alice" };
    private static readonly AccessContext Denied = new() { ObjectId = "bob", Name = "Bob" };

    private static readonly Address SecuredHub = new("SecuredHub");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(
                new MeshNode("SecuredHub") { Name = "Secured Hub" },
                // Only alice may read SecuredHub. bob has no assignment at all.
                AssignmentNodeFactory.UserRole(Permitted.ObjectId!, "Viewer", scope: "SecuredHub"))
            .ConfigureDefaultNodeHub(c => c.AddData(d => d));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddData(d => d);

    /// <summary>No blanket admin grant — the whole point is that bob genuinely has nothing.</summary>
    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    /// <summary>
    /// The permitted user reads FIRST. The denied user must still be denied — he must not be
    /// handed the stream the permitted user opened.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task DeniedUser_DoesNotInherit_PermittedUsersCachedStream()
    {
        var (client, accessService, workspace) = await ArrangeAsync();
        var reference = new CollectionsReference("test");

        ISynchronizationStream<EntityStore> permittedStream;
        using (accessService.SwitchAccessContext(Permitted))
            permittedStream = workspace.GetRemoteStream<EntityStore>(SecuredHub, reference);

        ISynchronizationStream<EntityStore> deniedStream;
        using (accessService.SwitchAccessContext(Denied))
            deniedStream = workspace.GetRemoteStream<EntityStore>(SecuredHub, reference);

        ReferenceEquals(deniedStream, permittedStream).Should().BeFalse(
            "a cached remote stream carries the permission view of whoever subscribed it — "
            + "handing bob alice's stream discloses content the owner would have denied him");

        var notification = await deniedStream
            .Timeout(10.Seconds())
            .Take(1)
            .Materialize()
            .Should().Within(30.Seconds()).Match(n => n.Kind == NotificationKind.OnError,
                "bob has no Read on SecuredHub, so his subscribe must fail closed");

        notification.Exception!.ToString().Should().Contain("Access denied",
            "bob's own subscribe must be refused by the owner's RLS gate, not silently satisfied "
            + "from alice's cached stream");

        GC.KeepAlive(client);
    }

    /// <summary>
    /// The denied user reads FIRST and errors. The permitted user must NOT inherit that errored
    /// stream — this is the "renders nothing" half of the same order-dependence.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task PermittedUser_DoesNotInherit_DeniedUsersFailedStream()
    {
        var (client, accessService, workspace) = await ArrangeAsync();
        var reference = new CollectionsReference("test");

        ISynchronizationStream<EntityStore> deniedStream;
        using (accessService.SwitchAccessContext(Denied))
            deniedStream = workspace.GetRemoteStream<EntityStore>(SecuredHub, reference);

        // Let bob's subscribe reach its terminal (denied) state before alice asks, so the cache
        // genuinely holds a failed stream rather than an in-flight one.
        await deniedStream
            .Timeout(10.Seconds())
            .Take(1)
            .Materialize()
            .Should().Within(30.Seconds()).Match(n => n.Kind == NotificationKind.OnError);

        ISynchronizationStream<EntityStore> permittedStream;
        using (accessService.SwitchAccessContext(Permitted))
            permittedStream = workspace.GetRemoteStream<EntityStore>(SecuredHub, reference);

        ReferenceEquals(permittedStream, deniedStream).Should().BeFalse(
            "alice must get her own subscribe; inheriting bob's refused stream renders her view empty");

        var notification = await permittedStream
            .Timeout(10.Seconds())
            .Take(1)
            .Materialize()
            .Should().Within(30.Seconds()).Match(_ => true);

        // With Read granted the stream may still error about unmapped collections — but never
        // about access (that would be bob's denial leaking into alice's view).
        if (notification.Kind == NotificationKind.OnError)
            notification.Exception!.ToString().Should().NotContain("Access denied",
                "alice holds Viewer on SecuredHub — an access denial here is bob's, inherited via the cache");

        GC.KeepAlive(client);
    }

    private async Task<(IMessageHub Client, AccessService Access, IWorkspace Workspace)> ArrangeAsync()
    {
        var client = GetClient();
        // Make sure the owning hub is up before we subscribe, so a NotFound cannot masquerade
        // as (or mask) an access denial.
        await client.Observe(new PingRequest(), o => o.WithTarget(SecuredHub))
            .Should().Within(30.Seconds()).Emit();
        return (client,
            client.ServiceProvider.GetRequiredService<AccessService>(),
            client.GetWorkspace());
    }
}
