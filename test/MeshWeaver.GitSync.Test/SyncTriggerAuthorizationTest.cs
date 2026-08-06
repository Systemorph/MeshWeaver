using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Pins #811 part D realized against the sync trigger: on a GitSynced (system-owned) Space the
/// <see cref="SystemOwnedAccessRetractionHandler"/> retracts every write-conferring grant, so NO
/// real principal holds Create on the partition — and the sync triggers
/// (<see cref="GitHubActivityExtensions.CheckBranchStateOnGitHub"/> /
/// <see cref="GitHubActivityExtensions.UpdateToLatestFromGitHub"/> /
/// <see cref="GitHubActivityExtensions.CommitToGitHub"/>) used to create their Activity node under
/// the CALLER's identity, dying with <i>"Access denied: Create permission required for node
/// '{space}/_Activity/…'"</i> for every legitimate user, even on the read-only <c>check</c>
/// (observed on memex 2026-08-06, the first morning after #805).
///
/// <para>The contract pinned here is #820's: <b>the click authorizes, the System executes.</b>
/// A signed-in principal whose ONLY holding on the Space is a read entitlement — the exact grant
/// shape the retraction handler deliberately leaves — may trigger <c>check</c> and <c>update</c>
/// (repo → space convergence; the repo is the source of truth), and the activity runs under the
/// System identity (its <c>CreatedBy</c> IS System). <c>commit</c> (space → repo) needs the
/// strongest authority a real principal can still hold — Update on the Space. A PLATFORM ADMIN
/// (the <c>Admin/_Access</c>-scoped grant — a platform capability, never on the Space's scope
/// walk) may trigger EVERY op, holding no per-space permission at all. An anonymous caller is
/// denied outright.</para>
///
/// <para>The principals deliberately have the PRODUCTION shape (post-#804/#805): the fixture
/// builds on <see cref="MonolithMeshTestBase.ConfigureMeshBase"/> (no root-scope
/// <c>Public → Admin</c> grant), and the triggering identities carry no <c>Roles=["Admin"]</c>
/// claim — the masking that hid this class of bug from every other GitSync test (#811 part C).</para>
/// </summary>
public class SyncTriggerAuthorizationTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    /// <summary>The GitSynced Space under test. Fixed name — grants are seeded statically on it.</summary>
    private const string Space = "SyncedSpace";

    /// <summary>Signed-in principal whose ONLY holding on the Space is a Viewer entitlement —
    /// the one grant shape <c>AccessAssignmentGuard.IsForbiddenOnSystemOwned</c> lets survive.</summary>
    private const string Reader = "sync-reader";

    /// <summary>The production-shaped platform admin: Admin on the Admin partition, nothing else
    /// (the exact node <c>UserOnboardingService.GrantPlatformAdmin</c> writes).</summary>
    private const string PlatformAdmin = "sync-admin";

    private const string RepoUrl = "https://github.com/test/synced-space";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureGitHubSync(ConfigureMeshBase(builder))
            .AddMeshNodes(
                AssignmentNodeFactory.UserRole(Reader, "Viewer", Space),
                AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", "Admin"));

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private static AccessContext Principal(string id) => new() { ObjectId = id, Name = id };

    /// <summary>Creates the Space, wires its GitHub sync (making it SYSTEM-OWNED — the retraction
    /// handler fires on the <c>_GitSync</c> create), and seeds one commit in the fake repo. Runs
    /// under the fixture's DevLogin claim identity, standing in for the provisioning flow.</summary>
    private async Task ProvisionSyncedSpace()
    {
        await Connect();
        await CreateSpace(Space, "Synced Space");
        await CreateMarkdown($"{Space}/Page", "Page", "# page");
        await Sync.SaveConfig(Space, RepoUrl, "main", null, true, true)
            .Timeout(30.Seconds()).ToTask();
        await Sync.SyncToGitHub(Space, UserId).Timeout(60.Seconds()).ToTask();
    }

    [Fact(Timeout = 120000)]
    public async Task Reader_WithOnlyAnEntitlement_TriggersCheckAndUpdate_ActivityRunsAsSystem()
    {
        await ProvisionSyncedSpace();
        // The reader's own GitHub credential — whose token talks to the (fake) GitHub API.
        await Credentials.Save(Reader, new GitHubToken("ghp_reader", null, "bearer", "repo", null), "reader")
            .Timeout(30.Seconds()).ToTask();

        // The seeded shape really is production's: Read via the entitlement, and NOT partition
        // write — the retraction regime means no user can hold Create/Update here, which is
        // exactly why the trigger must not require it.
        var effective = await Mesh.GetEffectivePermissions(Space, Reader)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        Assert.True(effective.HasFlag(Permission.Read), $"reader should Read the Space, got {effective}");
        Assert.False(effective.HasFlag(Permission.Create), $"reader must NOT hold Create, got {effective}");
        Assert.False(effective.HasFlag(Permission.Update), $"reader must NOT hold Update, got {effective}");

        // check — informational, any signed-in reader. Before the fix this died at the Activity
        // create ("Create permission required for '{Space}/_Activity/…'").
        Task<string> check;
        using (Access.SwitchAccessContext(Principal(Reader)))
            check = Mesh.CheckBranchStateOnGitHub(Space, Reader).Timeout(90.Seconds()).ToTask();
        var checkPath = await check;
        var checkLog = await WaitForActivity(checkPath, l => l.Status != ActivityStatus.Running);
        Assert.Equal(ActivityStatus.Succeeded, checkLog.Status);
        Assert.Contains(checkLog.Messages, m => m.Message.Contains("up to date"));

        // The System executes: the activity node is OWNED by the System identity, not the clicking
        // reader — that ownership is what lets every Append/Finish land on the system-owned space.
        var checkNode = await WaitForNode(checkPath);
        Assert.Equal(WellKnownUsers.System, checkNode.CreatedBy);

        // update — repo → space convergence, same Read-based authorization.
        Task<string> update;
        using (Access.SwitchAccessContext(Principal(Reader)))
            update = Mesh.UpdateToLatestFromGitHub(Space, Reader).Timeout(90.Seconds()).ToTask();
        var updatePath = await update;
        var updateLog = await WaitForActivity(updatePath, l => l.Status != ActivityStatus.Running);
        Assert.Equal(ActivityStatus.Succeeded, updateLog.Status);
    }

    [Fact(Timeout = 120000)]
    public async Task Commit_IsDeniedForReaderAndAnonymous_NeverForMissingPartitionWrite()
    {
        await ProvisionSyncedSpace();

        // The reader may check/update but NOT commit — space → repo needs commit authority.
        Task<string> readerCommit;
        using (Access.SwitchAccessContext(Principal(Reader)))
            readerCommit = Mesh.CommitToGitHub(Space, Reader).Timeout(60.Seconds()).ToTask();
        var deniedCommit = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => readerCommit);
        Assert.Contains("platform admin", deniedCommit.Message);

        // An anonymous caller is denied even the informational check — triggering still writes an
        // activity into the mesh and must have an authenticated principal behind it.
        Task<string> anonymousCheck;
        using (Access.SwitchAccessContext(Principal(WellKnownUsers.Anonymous)))
            anonymousCheck = Mesh.CheckBranchStateOnGitHub(Space, WellKnownUsers.Anonymous)
                .Timeout(60.Seconds()).ToTask();
        var deniedCheck = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => anonymousCheck);
        Assert.Contains("Sign-in required", deniedCheck.Message);
    }

    [Fact(Timeout = 120000)]
    public async Task PlatformAdmin_WithOnlyAdminScopedGrant_TriggersEveryOp_AsSystem()
    {
        await ProvisionSyncedSpace();
        await Credentials.Save(PlatformAdmin, new GitHubToken("ghp_admin", null, "bearer", "repo", null), "admin")
            .Timeout(30.Seconds()).ToTask();

        // A platform admin is NOT a data superuser (#811's pin): the Admin-partition grant confers
        // nothing on the Space itself — no Read, no Update. Every trigger authorization below must
        // therefore come from IsGlobalAdmin, never from a per-space permission (which the
        // retraction handler would have removed anyway).
        var onSpace = await Mesh.GetEffectivePermissions(Space, PlatformAdmin)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        Assert.False(onSpace.HasFlag(Permission.Update), $"platform admin must NOT hold Update on the Space, got {onSpace}");

        // check — the deploy persona must be able to ask branch state even with NO Read on the
        // Space (the exact shape of a platform admin running `git_hub_sync check` on a mesh).
        Task<string> check;
        using (Access.SwitchAccessContext(Principal(PlatformAdmin)))
            check = Mesh.CheckBranchStateOnGitHub(Space, PlatformAdmin).Timeout(90.Seconds()).ToTask();
        var checkLog = await WaitForActivity(await check, l => l.Status != ActivityStatus.Running);
        Assert.Equal(ActivityStatus.Succeeded, checkLog.Status);

        await CreateMarkdown($"{Space}/Extra", "Extra", "# extra");

        Task<string> commit;
        using (Access.SwitchAccessContext(Principal(PlatformAdmin)))
            commit = Mesh.CommitToGitHub(Space, PlatformAdmin).Timeout(90.Seconds()).ToTask();
        var commitPath = await commit;
        var commitLog = await WaitForActivity(commitPath, l => l.Status != ActivityStatus.Running);
        Assert.Equal(ActivityStatus.Succeeded, commitLog.Status);
        Assert.Contains(commitLog.Messages, m => m.Message.Contains("Committed"));
        Assert.Contains(Fake.Tree(RepoUrl), f => f.Path == "Extra.md");

        var commitNode = await WaitForNode(commitPath);
        Assert.Equal(WellKnownUsers.System, commitNode.CreatedBy);
    }

    private async Task<ActivityLog> WaitForActivity(string activityPath, Func<ActivityLog, bool> predicate) =>
        await Mesh.GetWorkspace().GetMeshNodeStream(activityPath)
            .Select(n => n?.Content as ActivityLog)
            .Where(l => l is not null && predicate(l))
            .Select(l => l!)
            .FirstAsync()
            .Timeout(60.Seconds())
            .ToTask();
}
