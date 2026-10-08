using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>On a partition with a ONE-WAY <c>_GitSync</c>, no role confers a write on its content —
/// not even an <c>Admin</c> grant that is still there</b> (#5140).
///
/// <para><b>The state this reproduces is the one measured live</b> on 2026-10-08: <c>Home</c> and
/// <c>Provider</c> on memex.meshweaver.cloud and <c>Deployments</c> on memex.systemorph.com each
/// carry a human <c>Admin</c> grant beside a one-way <c>_GitSync</c>, because their sync was wired
/// before the retraction sweep could retract it, and the sweep fires only when a sync config is
/// CREATED. Ownership was enforced through the grants alone, so a leftover grant still conferred
/// write: a direct edit of a <c>Deployments</c> record by that Admin had been accepted the day
/// before. Here the leftover grant is a CONFIGURED one, which the sweep cannot delete (it lists the
/// store) — so it survives the real sync wiring exactly as the live grants survived theirs.</para>
///
/// <para><b>Every seam, not one.</b> The update goes through <c>GetMeshNodeStream(..).Update</c>,
/// i.e. a patch whose only gate is the <c>[RequiresPermission]</c> delivery gate on the owner; the
/// delete goes through the RLS node validator; and the effective permissions are read directly, as
/// the menus read them. A fix in only one of those places fails the other two.</para>
///
/// <para><b>Controls, over the same grant and the same writes:</b> the Admin's update on a partition
/// with a BIJECTIVE sync lands (there the mesh nodes are the working copy); System's update on the
/// one-way partition lands (the importer must keep writing); and the Admin keeps Read. A rule that
/// refused every write near a <c>_GitSync</c>, or every non-admin, fails one of them.</para>
///
/// <para><b>Negative control,</b> run before committing: with the
/// <c>ObserveRepositoryOwnedContent</c> leg's cap removed from <c>PermissionEvaluator</c>, the three
/// refusal tests fail — the Admin's update and delete are accepted and the fold reports
/// Create/Update/Delete — while the controls pass unchanged.</para>
/// </summary>
public class SystemOwnedPartitionRefusesNonSystemWritesTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string OneWay = "RepoOwnedSpace";
    private const string Bijective = "WorkingCopySpace";
    private const string PartitionAdmin = "leftover-admin";
    private const string RepoUrl = "https://github.com/test/repo-owned";
    private const string HomeOwner = "home-owner";

    private static readonly AccessContext Admin = new() { ObjectId = PartitionAdmin, Name = "Leftover Admin" };

    private static TimeSpan Budget => TestTimeouts.Convergence;

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    // 🚨 ConfigureMeshBase, not base.ConfigureMesh: the latter grants Public the Admin role
    // everywhere, which would make the leftover grant irrelevant to every verdict.
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                return services;
            })
            .AddMeshNodes(
                new MeshNode(OneWay) { Name = "Repo-owned space", NodeType = "Markdown" },
                new MeshNode(Bijective) { Name = "Working-copy space", NodeType = "Markdown" },
                new MeshNode(HomeOwner) { Name = "A person's home", NodeType = "Markdown" },
                AdminGrant(OneWay),
                AdminGrant(Bijective));

    private static MeshNode AdminGrant(string partition) =>
        new($"{PartitionAdmin}_Access", $"{partition}/_Access")
        {
            NodeType = "AccessAssignment",
            Name = "Leftover Admin",
            MainNode = partition,
            Content = new AccessAssignment
            {
                AccessObject = PartitionAdmin,
                DisplayName = "Leftover Admin",
                Roles = [new RoleAssignment { Role = Role.Admin.Id }],
            },
        };

    /// <summary>
    /// A content page in <paramref name="partition"/>, created BEFORE the sync is wired (by the
    /// harness administrator), then the sync wired through the real service.
    /// </summary>
    private async Task<string> APageUnderASync(string partition, bool twoWay)
    {
        var path = $"{partition}/page{Guid.NewGuid().ToString("N")[..8]}";
        await NodeFactory.CreateNode(MeshNode.FromPath(path) with { NodeType = "Markdown", Name = "untouched" })
            .Should().Within(Budget).Emit("the page fixture must exist before the sync is wired",
                cancellationToken: TestContext.Current.CancellationToken);

        var config = await Sync.SaveConfig(
                partition, RepoUrl, "main", null, false, false,
                direction: twoWay ? SyncDirection.Bidirectional : SyncDirection.ImportOnly,
                twoWay: twoWay)
            .Should().Within(Budget).Emit("the sync config must be written",
                cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(!twoWay, AccessAssignmentGuard.IsSystemOwned(config, Mesh.JsonSerializerOptions));
        return path;
    }

    private void ActAs(AccessContext identity)
    {
        TestUsers.DevLogin(Mesh, identity);
        Access.SetContext(identity);
    }

    /// <summary>The write's outcome: null when it landed, the fault when it was refused.</summary>
    private static IObservable<Exception?> Outcome<T>(IObservable<T> write) =>
        write.Take(1)
            .Select(_ => (Exception?)null)
            .Catch((Exception ex) => Observable.Return<Exception?>(ex));

    private IObservable<Exception?> Rename(string path, string name) =>
        Outcome(Mesh.GetWorkspace().GetMeshNodeStream(path).Update(n => n with { Name = name }));

    /// <summary>
    /// The stored page, re-read until <paramref name="until"/> holds — straight from the store, so
    /// the read does not depend on whom the test is currently acting as.
    /// </summary>
    private Task<MeshNode> ReadStored(string path, Func<MeshNode, bool>? until = null) =>
        Observable.Interval(TestTimeouts.Quick / 10)
            .StartWith(0L)
            .SelectMany(_ => Storage.Read(path, Mesh.JsonSerializerOptions).Take(1))
            .Where(n => n is not null && (until?.Invoke(n) ?? true))
            .Select(n => n!)
            .Take(1)
            .Should().Within(Budget).Emit("the page must be in the store",
                cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>The permissions the fold settles on for the admin at <paramref name="path"/>.</summary>
    private Task<Permission> AdminPermissions(string path, Func<Permission, bool> until) =>
        PermissionEvaluator.GetEffectivePermissions(Mesh, path, PartitionAdmin)
            .Where(until)
            .Take(1)
            .Should().Within(Budget).Emit("the fold must settle",
                cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public async Task TheLeftoverAdmin_HoldsNoWrite_ButKeepsRead_OnAOneWaySyncedPartition()
    {
        var page = await APageUnderASync(OneWay, twoWay: false);

        // The fold's FIRST emission is its verdict for every one-shot gate, so that is what is read.
        var permissions = await AdminPermissions(page, _ => true);

        permissions.HasFlag(Permission.Read).Should().BeTrue("the leftover grant still confers Read");
        (permissions & (Permission.Create | Permission.Update | Permission.Delete)).Should().Be(Permission.None,
            "the content is its repository's projection, so no role may write it — the answer every "
            + "gate and menu reads (#5140)");
    }

    [Fact]
    public async Task TheLeftoverAdmin_CannotUpdateContent_OnAOneWaySyncedPartition()
    {
        var page = await APageUnderASync(OneWay, twoWay: false);

        ActAs(Admin);
        var refusal = await Rename(page, "edited by the admin")
            .Should().Within(Budget).Emit("the write must answer");

        refusal.Should().NotBeNull(
            "the partition is its repository's projection, so only the importer writes its content — "
            + "a leftover Admin grant must not confer write (#5140)");
        (await ReadStored(page)).Name.Should().Be("untouched");
    }

    [Fact]
    public async Task TheLeftoverAdmin_CannotDeleteContent_OnAOneWaySyncedPartition()
    {
        var page = await APageUnderASync(OneWay, twoWay: false);

        ActAs(Admin);
        var refusal = await Outcome(NodeFactory.DeleteNode(page))
            .Should().Within(Budget).Emit("the delete must answer");

        refusal.Should().NotBeNull(
            "deleting repo-owned content is a write like any other; the next sync would re-import it");
        (await ReadStored(page)).Name.Should().Be("untouched");
    }

    [Fact]
    public async Task System_StillUpdatesContent_OnAOneWaySyncedPartition()
    {
        var page = await APageUnderASync(OneWay, twoWay: false);

        Exception? refusal;
        using (Access.ImpersonateAsSystem())
            refusal = await Rename(page, "imported").Should().Within(Budget).Emit("the write must answer");

        refusal.Should().BeNull("the importer writes as System and must keep doing so");
        (await ReadStored(page, n => n.Name == "imported")).Name.Should().Be("imported");
    }

    [Fact]
    public async Task TheAdmin_StillUpdatesContent_OnABijectivelySyncedPartition()
    {
        var page = await APageUnderASync(Bijective, twoWay: true);

        ActAs(Admin);
        var refusal = await Rename(page, "edited in the working copy")
            .Should().Within(Budget).Emit("the write must answer");

        refusal.Should().BeNull(
            "a bijective sync is not system-owned: its mesh nodes are the working copy and editing "
            + "them is the point — the rule keys on the DIRECTION, not on the presence of a sync");
        (await ReadStored(page, n => n.Name == "edited in the working copy")).Name
            .Should().Be("edited in the working copy");
    }

    /// <summary>
    /// The RLS own-scope shortcut admits a person's writes to their OWN partition without the
    /// fold — so it is the one seam that would still admit a content create there once that home
    /// is synced one-way. Control: the same create, before the sync is wired, lands.
    /// </summary>
    [Fact]
    public async Task TheOwner_CannotCreateContent_InTheirOwnHome_OnceItIsSyncedOneWay()
    {
        var owner = new AccessContext { ObjectId = HomeOwner, Name = "Home owner" };
        ActAs(owner);
        var before = await Outcome(NodeFactory.CreateNode(
                MeshNode.FromPath($"{HomeOwner}/before") with { NodeType = "Markdown", Name = "before" }))
            .Should().Within(Budget).Emit("the create must answer");
        before.Should().BeNull("the control: an ordinary home is its owner's to write");

        Access.SetContext(null);
        await Sync.SaveConfig(HomeOwner, RepoUrl, "main", null, false, false,
                direction: SyncDirection.ImportOnly, twoWay: false)
            .Should().Within(Budget).Emit("the sync config must be written",
                cancellationToken: TestContext.Current.CancellationToken);

        ActAs(owner);
        var after = await Outcome(NodeFactory.CreateNode(
                MeshNode.FromPath($"{HomeOwner}/after") with { NodeType = "Markdown", Name = "after" }))
            .Should().Within(Budget).Emit("the create must answer");
        after.Should().NotBeNull(
            "once the home is synced one-way its content is the repository's — the own-scope "
            + "shortcut must not be the seam where that rule stops holding (#5140)");
    }
}
