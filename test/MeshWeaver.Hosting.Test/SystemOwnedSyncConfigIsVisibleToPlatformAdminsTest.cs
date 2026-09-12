using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A platform admin can ALWAYS see and delete a Space's GitHub-sync config — including on a
/// SYSTEM-OWNED Space, where nobody can be granted anything.</b> A non-admin still cannot.
///
/// <para>Measured on memex.meshweaver.cloud 2026-09-12: <c>MeshWeaver/_GitSync</c> — a config the
/// platform itself had created, in a Space owned by <c>system-security</c>, re-importing the whole
/// core repository on every green build (Memex#237) — answered <c>Not found</c> to the platform
/// admin's <c>get</c> and <i>"Delete permission denied for 'MeshWeaver/_GitSync'"</i> to his
/// <c>delete</c>, while the webhook log listed it among the mesh's 70 sync configs. The Space is
/// system-owned (one-way sync), so <c>AccessAssignmentGuard.IsForbiddenOnSystemOwned</c> refuses
/// every write grant on it and there is no ordinary way IN; and a platform admin is deliberately
/// not a data superuser, so the Admin-partition grant reaches nothing there either. The config was
/// unremovable through any API.</para>
///
/// <para>The tests below hold the fold FIXED — the admin holds NO Read on the config path, asserted
/// as a verdict — so the widening can only come from the node type's own rules
/// (<c>GitHubSyncConfigAccessRule</c>, <c>SpaceAccessRule.ReadAccess</c>), and the negative half is
/// non-vacuous by construction: the same row is read back under the admin identity FIRST.</para>
///
/// <para>🚨 <c>ConfigureMeshBase</c>, not <c>base.ConfigureMesh</c>: the latter chains
/// <c>PublicAdminAccess()</c>, which grants Public the Admin role in every default partition — under
/// it the non-admin would hold Read outright and the negative assertions would pass proving
/// nothing.</para>
/// </summary>
public class SystemOwnedSyncConfigIsVisibleToPlatformAdminsTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>The Admin partition: an Admin grant HERE is the platform-admin shape (<c>hub.IsGlobalAdmin</c>).</summary>
    private const string AdminPartition = "Admin";

    /// <summary>A platform admin: <c>Permission.All</c> at scope <c>Admin</c>, and nothing on any Space.</summary>
    private const string PlatformAdmin = "platform-boss";

    /// <summary>An ordinary user with a real grant on their OWN partition, and nothing on Admin.</summary>
    private const string PlainUser = "plain-jane";

    private const string RepoUrl = "https://github.com/test/system-owned";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(AdminPartition) { Name = "Admin", NodeType = "Markdown" },
                new MeshNode(PlainUser) { Name = "Plain Jane", NodeType = "Markdown" },
                // THE platform-admin shape: the Admin role in the Admin partition's _Access
                // namespace. Not a root grant — that is the data-superuser shape and deliberately
                // not how platform admins are provisioned.
                AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", AdminPartition),
                // The ordinary user owns their own partition and holds nothing on Admin.
                AssignmentNodeFactory.UserRole(PlainUser, "Admin", PlainUser))
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                return services;
            });

    private static AccessContext Identity(string userId) => new() { ObjectId = userId, Name = userId };

    /// <summary>
    /// A Space with a ONE-WAY sync — i.e. system-owned. Created under the harness's DevLogin
    /// identity (a root grant, so the create is allowed); neither test subject holds anything on it.
    /// </summary>
    private async Task<string> PrepareSystemOwnedSpace()
    {
        var space = "Owned" + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space", Name = "System-owned space", State = MeshNodeState.Active,
            Content = new Space(),
        }).Should().Within(Budget).Emit("the Space fixture must be created");

        var config = await Sync.SaveConfig(space, RepoUrl, "main", null, false, false,
                direction: SyncDirection.ImportOnly, twoWay: false)
            .Should().Within(Budget).Emit("the one-way sync config must be written");
        Assert.Equal(GitHubSyncService.ConfigPath(space), config.Path);
        Assert.Equal(GitHubSyncService.ConfigNodeType, config.NodeType);
        Assert.True(AccessAssignmentGuard.IsSystemOwned(config, Mesh.JsonSerializerOptions),
            "a one-way _GitSync is THE definition of system-owned");
        return space;
    }

    /// <summary>The RLS-filtered read the MCP <c>get</c> and every listing ride: the rows at
    /// <paramref name="path"/> as <paramref name="viewer"/> sees them.</summary>
    private IObservable<bool> IsVisibleTo(string path, string viewer)
        => NodeFactory
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}").ForViewer(viewer))
            .Select(change => change.Items.Any(n =>
                string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)));

    private IObservable<DeleteNodeResponse> DeleteAs(string path, string viewer)
        => ObserveNodeOperation(
                new DeleteNodeRequest(path),
                o => o.WithAccessContext(Identity(viewer)))
            .Select(d => d.Message)
            .Take(1);

    /// <summary>
    /// The two identities, stated as verdicts before anything is read through them — and the fold
    /// itself, FIXED: the admin's Admin-partition grant confers no Read on the config path. What the
    /// tests below see through the RLS filter is therefore the node type's rule, nothing else.
    /// </summary>
    private async Task AssertTheFoldIsUnchanged(string configPath)
    {
        await Mesh.IsGlobalAdmin(PlatformAdmin).Should().Within(Budget).Match(a => a,
            "an Admin-partition grant IS the platform-admin predicate");
        await Mesh.IsGlobalAdmin(PlainUser).Should().Within(Budget).Match(a => !a,
            "owning your own partition does not make you a platform admin");
        await Mesh.GetEffectivePermissions(configPath, PlatformAdmin).Should().Within(Budget)
            .Match(p => !p.HasFlag(Permission.Read) && !p.HasFlag(Permission.Delete),
                "a platform admin is NOT a data superuser: the fold on a system-owned Space stays "
                + "None for them — the widening must come from the node type's rule, never from a grant");
        await Mesh.GetEffectivePermissions(configPath, PlainUser).Should().Within(Budget)
            .Match(p => !p.HasFlag(Permission.Read) && !p.HasFlag(Permission.Delete),
                "the non-admin holds nothing on somebody else's Space");
    }

    /// <summary>
    /// 🚨 <b>THE READ HALF.</b> The sync config AND the system-owned Space's root node are visible
    /// to the platform admin through the RLS-filtered read, and to nobody else. Admin first — the
    /// positive control that makes the non-admin's empty answer a VERDICT rather than a timing story.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task APlatformAdmin_SeesTheSyncConfigAndItsSystemOwnedSpace_ANonAdminSeesNeither()
    {
        var space = await PrepareSystemOwnedSpace();
        var configPath = GitHubSyncService.ConfigPath(space);
        await AssertTheFoldIsUnchanged(configPath);

        await IsVisibleTo(configPath, PlatformAdmin).Should().Within(Budget).Match(seen => seen,
            "the platform admin must be able to SEE the config that re-imports a repository");
        await IsVisibleTo(space, PlatformAdmin).Should().Within(Budget).Match(seen => seen,
            "the platform admin must be able to see that the system-owned Space EXISTS");

        var configSeenByPlainUser = await IsVisibleTo(configPath, PlainUser)
            .Should().Within(Budget).Emit("the query itself must answer, so the emptiness is a verdict");
        Assert.False(configSeenByPlainUser, "a non-admin still cannot see somebody else's sync config");
        var spaceSeenByPlainUser = await IsVisibleTo(space, PlainUser)
            .Should().Within(Budget).Emit("the query itself must answer, so the emptiness is a verdict");
        Assert.False(spaceSeenByPlainUser, "a non-admin still cannot see a Space they hold nothing on");
    }

    /// <summary>
    /// 🚨 <b>THE DELETE HALF — Memex#237's blocker.</b> The non-admin's delete is refused with a
    /// permission verdict (not an availability failure); the platform admin's succeeds, and the
    /// config is gone from storage afterwards.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task APlatformAdmin_CanDeleteTheSyncConfigOfASystemOwnedSpace_ANonAdminCannot()
    {
        var space = await PrepareSystemOwnedSpace();
        var configPath = GitHubSyncService.ConfigPath(space);
        await AssertTheFoldIsUnchanged(configPath);

        var refused = await DeleteAs(configPath, PlainUser)
            .Should().Within(Budget).Emit("the delete must answer");
        Assert.False(refused.Success, "a non-admin must not be able to remove somebody else's sync");
        Assert.Equal(NodeDeletionRejectionReason.Unauthorized, refused.RejectionReason);
        Assert.NotNull(await NodeTypeAccessRuleGate.ReadSubjectNode(Mesh, configPath)
            .Should().Within(Budget).Emit("the refused delete must have left the config in place"));

        var deleted = await DeleteAs(configPath, PlatformAdmin)
            .Should().Within(Budget).Emit("the delete must answer");
        Assert.True(deleted.Success,
            $"the platform admin's delete must succeed, but was refused: {deleted.Error}");
        var afterwards = await NodeTypeAccessRuleGate.ReadSubjectNode(Mesh, configPath)
            .Should().Within(Budget).Emit("storage must answer about the deleted path");
        Assert.Null(afterwards);
    }
}
