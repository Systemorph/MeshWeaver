using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A platform admin can ISSUE a grant on a Space nobody else can grant on — a SYSTEM-OWNED
/// (one-way GitSynced) Space, or an OWNERLESS one — and on no other (MeshWeaver#5904).</b>
///
/// <para>Measured on partnerre-control 2026-09-29/30: the platform admin created Space
/// <c>Deployments</c> over MCP and wired its <c>_GitSync</c>; the retraction took the creator's Admin
/// away (a system-owned Space grants write to the importer only), so <c>Deployments</c> carried NO
/// grant. <c>create Deployments/_Access/rbuergi_Access</c> (Viewer) was refused with "…anyone else
/// needs a platform admin to grant access under 'Deployments/_Access'" — by the platform admin. The
/// records under it became unreadable to every approver, so no InstanceAction could be approved.</para>
///
/// <para>What holds, beside the widening: an Admin grant on a system-owned Space is STILL refused
/// (<c>AccessAssignmentGuard.IsForbiddenOnSystemOwned</c> — only an entitlement is issuable there); a
/// non-admin still cannot grant; and a platform admin still cannot grant on a Space that HAS an
/// owner — they are not a data superuser, and an owned Space's owner is the one who grants.</para>
///
/// <para>🚨 <c>ConfigureMeshBase</c>, not <c>base.ConfigureMesh</c>: the latter chains
/// <c>PublicAdminAccess()</c>, under which every subject holds Admin everywhere and every refusal
/// asserted below would pass proving nothing.</para>
/// </summary>
public class PlatformAdminCanGrantOnAnUngrantableSpaceTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string AdminPartition = "Admin";

    /// <summary>A platform admin: <c>Permission.All</c> at scope <c>Admin</c>, nothing on any Space.</summary>
    private const string PlatformAdmin = "platform-boss";

    /// <summary>An ordinary user with a grant on their OWN partition only.</summary>
    private const string PlainUser = "plain-jane";

    /// <summary>A partition whose only grant is a CONFIGURED (static) node.</summary>
    private const string ConfiguredPartition = "ConfiguredOnly";

    private const string RepoUrl = "https://github.com/test/ungrantable";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(AdminPartition) { Name = "Admin", NodeType = "Markdown" },
                new MeshNode(PlainUser) { Name = "Plain Jane", NodeType = "Markdown" },
                AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", AdminPartition),
                AssignmentNodeFactory.UserRole(PlainUser, "Admin", PlainUser),
                // A partition owned through CONFIGURATION only: its grant is a static node, never
                // in the store.
                new MeshNode(ConfiguredPartition) { Name = "Configured", NodeType = "Markdown" },
                AssignmentNodeFactory.UserRole("configured-owner", "Admin", ConfiguredPartition))
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                return services;
            });

    private static AccessContext Identity(string userId) => new() { ObjectId = userId, Name = userId };

    private async Task<string> CreateSpace(string prefix, bool exactName = false)
    {
        var space = exactName ? prefix : prefix + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space", Name = space, State = MeshNodeState.Active, Content = new Space(),
        }).Should().Within(Budget).Emit("the Space fixture must be created",
            cancellationToken: TestContext.Current.CancellationToken);
        return space;
    }

    /// <summary>The issue's shape: a Space whose ONE-WAY <c>_GitSync</c> makes it system-owned.</summary>
    private async Task<string> PrepareSystemOwnedSpace(string? exactName = null)
    {
        var space = exactName is null
            ? await CreateSpace("Deployments")
            : await CreateSpace(exactName, exactName: true);
        var config = await Sync.SaveConfig(space, RepoUrl, "main", null, false, false,
                direction: SyncDirection.ImportOnly, twoWay: false)
            .Should().Within(Budget).Emit("the one-way sync config must be written",
                cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(AccessAssignmentGuard.IsSystemOwned(config, Mesh.JsonSerializerOptions),
            "a one-way _GitSync is THE definition of system-owned");
        return space;
    }

    /// <summary>
    /// The #638 residue: a Space whose every grant is gone from the store (its creator grant never
    /// landed). Removed straight from storage — the state is, by definition, one no API writes.
    /// </summary>
    private async Task<string> PrepareOwnerlessSpace(string? exactName = null)
    {
        var space = exactName is null
            ? await CreateSpace("Ownerless")
            : await CreateSpace(exactName, exactName: true);
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var grants = await storage.ListChildPaths($"{space}/_Access").Take(1)
            .Should().Within(Budget).Emit("the grant folder must list",
                cancellationToken: TestContext.Current.CancellationToken);
        var paths = grants.NodePaths.ToArray();
        if (paths.Length > 0)
            await storage.DeleteMany(paths).Take(1).Should().Within(Budget)
                .Emit("the grants must be removed", cancellationToken: TestContext.Current.CancellationToken);
        var after = await storage.ListChildPaths($"{space}/_Access").Take(1)
            .Should().Within(Budget).Emit("the grant folder must list",
                cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(after.NodePaths);
        return space;
    }

    private IObservable<CreateNodeResponse> GrantAs(string caller, string space, string subject, string role)
    {
        var grant = AssignmentNodeFactory.UserRole(subject, role, space);
        return ObserveNodeOperation(
                new CreateNodeRequest(grant) { CreatedBy = caller },
                o => o.WithAccessContext(Identity(caller)))
            .Select(d => d.Message)
            .Take(1);
    }

    private async Task<CreateNodeResponse> Answer(
        IObservable<CreateNodeResponse> create, System.Threading.CancellationToken ct)
        => await create.Should().Within(Budget).Emit("the create must answer", cancellationToken: ct);

    private async Task AssertIsPlatformAdmin()
    {
        await Mesh.IsGlobalAdmin(PlatformAdmin).Should().Within(Budget).Match(a => a,
            "an Admin-partition grant IS the platform-admin predicate",
            cancellationToken: TestContext.Current.CancellationToken);
        await Mesh.IsGlobalAdmin(PlainUser).Should().Within(Budget).Match(a => !a,
            "owning your own partition does not make you a platform admin",
            cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 🚨 THE ISSUE. On a system-owned Space the platform admin issues themselves a Viewer
    /// entitlement — and then READS the Space's records, which is what the InstanceAction approval
    /// needed. Before the grant the fold holds nothing: the read comes from the grant, never from
    /// being a platform admin.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task APlatformAdmin_GrantsAViewerEntitlement_OnASystemOwnedSpace()
    {
        var space = await PrepareSystemOwnedSpace();
        await AssertIsPlatformAdmin();
        await Mesh.GetEffectivePermissions($"{space}/partnerre-test", PlatformAdmin).Should().Within(Budget)
            .Match(p => !p.HasFlag(Permission.Read),
                "a platform admin is NOT a data superuser — no Read before an explicit grant",
                cancellationToken: TestContext.Current.CancellationToken);

        var granted = await Answer(GrantAs(PlatformAdmin, space, PlatformAdmin, "Viewer"), TestContext.Current.CancellationToken);
        Assert.True(granted.Success,
            $"the platform admin's Viewer entitlement on the system-owned Space must be accepted — "
            + $"the refusal itself prescribes it — but was refused: {granted.Error}");

        await Mesh.GetEffectivePermissions($"{space}/partnerre-test", PlatformAdmin).Should().Within(Budget)
            .Match(p => p.HasFlag(Permission.Read) && !p.HasFlag(Permission.Update),
                "the entitlement confers Read and nothing more",
                cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The widening is an ENTITLEMENT on a system-owned Space, never an ownership claim: an Admin
    /// grant is still refused by the system-owned guard, for the platform admin too.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task APlatformAdmin_StillCannotGrantAdmin_OnASystemOwnedSpace()
    {
        var space = await PrepareSystemOwnedSpace();
        await AssertIsPlatformAdmin();

        var refused = await Answer(GrantAs(PlatformAdmin, space, PlatformAdmin, "Admin"), TestContext.Current.CancellationToken);
        Assert.False(refused.Success, "an Admin grant on a system-owned Space must stay refused");
        Assert.Contains("SYSTEM-OWNED", refused.Error);
    }

    /// <summary>A non-admin gains nothing: their grant on a system-owned Space stays refused.</summary>
    [Fact(Timeout = 240_000)]
    public async Task ANonAdmin_StillCannotGrant_OnASystemOwnedSpace()
    {
        var space = await PrepareSystemOwnedSpace();
        await AssertIsPlatformAdmin();

        var refused = await Answer(GrantAs(PlainUser, space, PlainUser, "Viewer"), TestContext.Current.CancellationToken);
        Assert.False(refused.Success, "a non-admin must not be able to grant on a Space they hold nothing on");
        Assert.Contains("Access denied", refused.Error);
    }

    /// <summary>
    /// The OWNERLESS half: the refusal names "a platform admin" as the repair, so the platform
    /// admin's grant is accepted. A non-admin's is not.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task APlatformAdmin_GrantsOnAnOwnerlessSpace_ANonAdminCannot()
    {
        var space = await PrepareOwnerlessSpace();
        await AssertIsPlatformAdmin();

        var refused = await Answer(GrantAs(PlainUser, space, PlainUser, "Viewer"), TestContext.Current.CancellationToken);
        Assert.False(refused.Success, "a non-admin must not be able to grant on an ownerless Space");
        Assert.Contains("NO access grants", refused.Error);

        var granted = await Answer(GrantAs(PlatformAdmin, space, PlatformAdmin, "Viewer"), TestContext.Current.CancellationToken);
        Assert.True(granted.Success,
            $"the platform admin's grant on an ownerless Space is the repair the refusal names, but was "
            + $"refused: {granted.Error}");
    }

    /// <summary>
    /// 🚨 THE BOUNDARY. A Space that HAS an owner (its creator's Admin grant) and no one-way sync is
    /// not the platform admin's to grant on — the widening must not turn "platform admin" into
    /// "grants themselves into anybody's Space".
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task APlatformAdmin_CannotGrant_OnAnOwnedSpace()
    {
        var space = await CreateSpace("Owned");
        await AssertIsPlatformAdmin();
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var grants = await storage.ListChildPaths($"{space}/_Access").Take(1)
            .Should().Within(Budget).Emit("the grant folder must list",
                cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(grants.NodePaths);

        var refused = await Answer(GrantAs(PlatformAdmin, space, PlatformAdmin, "Viewer"), TestContext.Current.CancellationToken);
        Assert.False(refused.Success, "an owned Space's owner is the one who grants — not a platform admin");
        Assert.Contains("Access denied", refused.Error);
    }

    /// <summary>
    /// The production shape of #5904: the platform admin grants the Viewer entitlement to a
    /// DIFFERENT subject (the approver). Read follows for the GRANTEE — and still not for the
    /// grantor, who issued a grant and received nothing.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task APlatformAdmin_GrantsViewerToAnotherSubject_TheGranteeReads_TheGrantorDoesNot()
    {
        const string approver = "approver-ann";
        var space = await PrepareSystemOwnedSpace();
        await AssertIsPlatformAdmin();
        var record = $"{space}/partnerre-test";
        await Mesh.GetEffectivePermissions(record, approver).Should().Within(Budget)
            .Match(p => !p.HasFlag(Permission.Read), "the approver holds nothing before the grant",
                cancellationToken: TestContext.Current.CancellationToken);

        var granted = await Answer(GrantAs(PlatformAdmin, space, approver, "Viewer"),
            TestContext.Current.CancellationToken);
        Assert.True(granted.Success, $"the platform admin's grant to the approver was refused: {granted.Error}");

        await Mesh.GetEffectivePermissions(record, approver).Should().Within(Budget)
            .Match(p => p.HasFlag(Permission.Read) && !p.HasFlag(Permission.Update),
                "the GRANTEE reads, and only reads",
                cancellationToken: TestContext.Current.CancellationToken);
        await Mesh.GetEffectivePermissions(record, PlatformAdmin).Should().Within(Budget)
            .Match(p => !p.HasFlag(Permission.Read),
                "the GRANTOR received nothing — issuing a grant is not holding one",
                cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 🚨 The platform's OWN partitions are refused outright, even in exactly the state the repair
    /// otherwise opens (system-owned, no grant): <c>PlatformAdminGrantRepair.IsPlatformPartition</c>.
    /// The positive control is <see cref="APlatformAdmin_GrantsAViewerEntitlement_OnASystemOwnedSpace"/>
    /// — the same shape under an ordinary name is granted.
    /// </summary>
    [Theory(Timeout = 240_000)]
    [InlineData("system-security")]
    [InlineData("Anonymous")]
    public async Task APlatformAdmin_CannotGrant_OnAPlatformPartition(string platformPartition)
    {
        var space = await PrepareSystemOwnedSpace(platformPartition);
        await AssertIsPlatformAdmin();

        var refused = await Answer(GrantAs(PlatformAdmin, space, PlatformAdmin, "Viewer"),
            TestContext.Current.CancellationToken);
        Assert.False(refused.Success, $"a grant on the platform partition '{space}' must be refused");
        Assert.Contains("Access denied", refused.Error);
    }

    /// <summary>
    /// A FLEET partition (here an ownerless <c>Ops</c>, no sync) takes an entitlement from the
    /// platform admin and NOTHING more — the cap holds where no system-owned guard would.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task APlatformAdmin_OnAnOwnerlessFleetPartition_GrantsViewerButNotAdmin()
    {
        var space = await PrepareOwnerlessSpace("Ops");
        await AssertIsPlatformAdmin();

        var admin = await Answer(GrantAs(PlatformAdmin, space, PlatformAdmin, "Admin"),
            TestContext.Current.CancellationToken);
        Assert.False(admin.Success, "an ownerless fleet partition must not become the platform admin's");
        Assert.Contains("Access denied", admin.Error);

        var viewer = await Answer(GrantAs(PlatformAdmin, space, PlatformAdmin, "Viewer"),
            TestContext.Current.CancellationToken);
        Assert.True(viewer.Success, $"the Viewer entitlement on the fleet partition was refused: {viewer.Error}");
    }

    /// <summary>
    /// 🚨 The fleet cap fails CLOSED on content: a grant whose content does not read as an
    /// AccessAssignment gets no repair. Driven through <c>PlatformAdminGrantRepair.MayIssue</c> itself
    /// so no later validator can be the one that refuses; the Viewer grant on the same ownerless
    /// fleet partition is the positive control.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task TheRepair_FailsClosed_OnAGrantWithoutReadableContent()
    {
        var space = await PrepareOwnerlessSpace("Feedback");
        await AssertIsPlatformAdmin();

        IObservable<bool> MayIssue(MeshNode grant) => PlatformAdminGrantRepair.MayIssue(Mesh,
            new NodeValidationContext
            {
                Operation = NodeOperation.Create,
                Node = grant,
                AccessContext = Identity(PlatformAdmin),
            },
            PlatformAdmin);

        var viewer = AssignmentNodeFactory.UserRole(PlatformAdmin, "Viewer", space);
        await MayIssue(viewer).Should().Within(Budget).Match(ok => ok,
            "positive control: a Viewer entitlement on an ownerless fleet partition is issuable",
            cancellationToken: TestContext.Current.CancellationToken);
        await MayIssue(viewer with { Content = null }).Should().Within(Budget).Match(ok => !ok,
            "a grant without readable content must never be repaired through",
            cancellationToken: TestContext.Current.CancellationToken);
        await MayIssue(AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", space))
            .Should().Within(Budget).Match(ok => !ok,
                "an Admin grant on a fleet partition is capped",
                cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A denied write into a partition owned only through CONFIGURATION says nothing beyond the
    /// denial: it is neither ownerless nor a store/fold disagreement (its grant is not in the store).
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ADenial_OnAConfiguredGrantPartition_CarriesNoDiagnosis()
    {
        var page = new MeshNode("page", ConfiguredPartition) { NodeType = "Markdown", Name = "page" };
        var refused = await Answer(ObserveNodeOperation(
                    new CreateNodeRequest(page) { CreatedBy = PlainUser },
                    o => o.WithAccessContext(Identity(PlainUser)))
                .Select(d => d.Message).Take(1),
            TestContext.Current.CancellationToken);
        Assert.False(refused.Success, "the plain user holds nothing on the configured partition");
        Assert.Contains("Access denied", refused.Error);
        Assert.DoesNotContain("NO access grants", refused.Error);
        Assert.DoesNotContain("holds a node", refused.Error);
    }

    /// <summary>
    /// 🚨 The two partition sets the repair keys on, pinned by FULL membership. They mirror MeshWeaver.Plugins
    /// <c>Hosting/InstanceAction/Source/DeleteSpaceRunner.cs</c> <c>ProtectedPartitions</c> (Platform ∪
    /// Fleet ∪ the `_` namespaces); a change to either list must change this fact and that one together.
    /// </summary>
    [Fact]
    public void ThePlatformAndFleetSets_AreExactlyTheProtectedPartitions()
    {
        Assert.Equal(
            new[] { "Admin", "Anonymous", "ApiToken", "Auth", "Kernel", "Portal", "User", "system-security" },
            WellKnownPartitions.Platform.OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            new[]
            {
                "AI", "Agent", "Approvals", "Deployments", "Doc", "Documentation", "Essentials", "Feedback",
                "Governance", "Home", "Hosting", "Hosting.Instance", "Model", "Ops", "Plugins", "Provider",
                "Providers", "Skill", "Store",
            },
            WellKnownPartitions.Fleet.OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.Empty(WellKnownPartitions.Platform.Intersect(WellKnownPartitions.Fleet));
        Assert.All(WellKnownPartitions.Platform, p => Assert.True(PlatformAdminGrantRepair.IsPlatformPartition(p)));
        Assert.All(WellKnownPartitions.Fleet, p => Assert.False(PlatformAdminGrantRepair.IsPlatformPartition(p)));
    }
}
