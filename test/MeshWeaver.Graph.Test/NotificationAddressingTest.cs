using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>THE ADDRESSEE OWNS THE DELIVERY PARTITION, AND THE PLATFORM BELL IS NOT PUBLIC.</b>
/// Systemorph/MeshWeaver#3156 (the write side) and #3216 (the read side) — one change set, because
/// each alone leaves the other broken or dangerous.
///
/// <para><b>What was wrong.</b> A notification was written as a satellite of the ENTITY it is about
/// — <c>{entity}/_Notification/{id}</c>, in THAT entity's partition — so the bell had no first
/// segment to name and, on Postgres, became a <c>UNION ALL</c> over every row of
/// <c>public.searchable_schemas</c>: 444 199-schema unions per five minutes across eight pods on
/// memex-cloud, 4.0 s each, while the database sat at 94–98 % CPU. And because <c>Admin</c> is
/// deliberately EXCLUDED from <c>searchable_schemas</c>, that fan-out could not read
/// <c>admin.notifications</c> AT ALL — every startup error, failed reconcile and stuck-instance
/// report addressed to platform operators was written, versioned, and shown to nobody.</para>
///
/// <para><b>The asymmetry this suite exists for.</b> The bug was "admins see nothing". The careless
/// fix is "everyone sees admin notifications", and that is the WORSE outcome — platform
/// notifications carry startup errors, failed reconciles and stuck-instance detail.
/// <see cref="PlatformNotification_IsNotReadableByANonAdmin"/> is the assertion that must fail if
/// anyone ever widens the platform bell, and it is the point of the change set rather than an
/// extra. It is deliberately NOT a settle-by-silence check: the same row is proven READABLE by a
/// platform admin in the same test, so an empty answer for the non-admin cannot be an artefact of
/// the row not being there yet.</para>
///
/// <para>Doc/Architecture/AddressedNotifications.</para>
/// </summary>
public class NotificationAddressingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string AdminPartition = NotificationService.PlatformAddressee;

    /// <summary>A platform admin: <c>Permission.All</c> at scope <c>Admin</c> — the ONE canonical
    /// predicate (<c>hub.IsGlobalAdmin</c>), never an ad-hoc role-name or root-scope check.</summary>
    private const string PlatformAdmin = "platform-boss";

    /// <summary>An ordinary user with a real grant on their OWN partition, and nothing on Admin.</summary>
    private const string PlainUser = "plain-jane";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private IWorkspace Workspace => Mesh.ServiceProvider.GetRequiredService<IWorkspace>();
    private System.Text.Json.JsonSerializerOptions Json => Mesh.JsonSerializerOptions;

    // 🚨 ConfigureMeshBase, not base.ConfigureMesh: the latter chains PublicAdminAccess(), which
    // grants Public the Admin role in every default partition — under it the non-admin would hold
    // Read on Admin outright and the negative assertion would pass vacuously while proving nothing.
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
                AssignmentNodeFactory.UserRole(PlainUser, "Admin", PlainUser));

    private static AccessContext Identity(string userId) => new() { ObjectId = userId, Name = userId };

    /// <summary>
    /// Dispatches and reads the written node back. 🚨 The read-back runs AS SYSTEM
    /// (<c>RunAsSystem</c>, never <c>Observable.Using</c> around an impersonation scope — #1790):
    /// the tests below deliberately switch the ambient identity, and a fixture probe that depended
    /// on whichever identity a previous test left behind would make the assertions order-dependent.
    /// </summary>
    private IObservable<MeshNode> Dispatch(string? recipient, string entity, string title)
        => NotificationService
            .Dispatch(Mesh, recipient, entity, title, "body", NotificationType.System, targetNodePath: entity)
            .SelectMany(_ => Access.RunAsSystem(() => Workspace
                .GetQuery($"addressing|{title}", NotificationService.BellQuery(recipient))
                .Select(nodes => (nodes ?? []).FirstOrDefault(n =>
                    n.ContentAs<Notification>(Json)?.Title == title))
                .Where(n => n is not null)
                .Select(n => n!)));

    // ── the pure rule ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 A <c>null</c> recipient is the PLATFORM, not "wherever the entity lives" and not
    /// "everybody". That is the fail-closed reading, and it is the one line that decides where an
    /// operator notification lands.
    /// </summary>
    [Theory]
    [InlineData(null, AdminPartition)]
    [InlineData("", AdminPartition)]
    [InlineData("   ", AdminPartition)]
    [InlineData("rbuergi", "rbuergi")]
    // A recipient given as a PATH resolves to its partition — the delivery root is always one segment.
    [InlineData("rbuergi/Documents/spec", "rbuergi")]
    [InlineData("/rbuergi", "rbuergi")]
    [InlineData(AdminPartition, AdminPartition)]
    public void ResolveAddressee_IsThePartition_OrThePlatform(string? recipient, string expected)
        => Assert.Equal(expected, NotificationService.ResolveAddressee(recipient));

    /// <summary>
    /// 🚨 <b>The bell shape must PIN, and pinning is what reaches <c>Admin</c>.</b> A single
    /// concrete <c>namespace:</c> is folded into <see cref="ParsedQuery.Path"/> by the parser, which
    /// is precisely the property <c>PostgreSqlPartitionedMeshQuery.ResolvePinnedPartition</c> reads
    /// to route the query to ONE schema without consulting <c>public.searchable_schemas</c> —
    /// exactly what <c>SecurityQueries.PartitionAssignments</c> relies on, and the reason the Admin
    /// special case there is gone rather than duplicated.
    ///
    /// <para>The alternation <c>namespace:{viewer}/_Notification|Admin/_Notification</c> is
    /// deliberately NOT what this builds. It classifies as anchored, but it leaves Path null, so it
    /// takes the FAN-OUT route where the namespace narrowing INTERSECTS with
    /// <c>searchable_schemas</c> — by design, so that a namespace anchor can never make an excluded
    /// schema newly visible — and <c>admin</c> is dropped again, silently. Two pinned reads, merged
    /// by the reader, is the only shape that reaches both bells.</para>
    /// </summary>
    [Theory]
    [InlineData("rbuergi", "rbuergi/_Notification")]
    [InlineData(AdminPartition, "Admin/_Notification")]
    [InlineData(null, "Admin/_Notification")]
    public void BellQuery_FoldsToASinglePathSoThePlannerCanPinIt(string? addressee, string expectedPath)
    {
        var parsed = new QueryParser().Parse(NotificationService.BellQuery(addressee));
        Assert.Equal(expectedPath, parsed.Path);
        // Not a multi-path alternation, and not a declared fan-out: one partition, named.
        Assert.False(parsed.CrossPartition);
        Assert.True(parsed.Paths is null or { Count: <= 1 });
    }

    // ── the write side (#3156) ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The notification lands in the ADDRESSEE's partition even when the entity it is about lives
    /// somewhere else entirely — which is the whole point: <c>TargetNodePath</c> keeps the entity,
    /// so grouping and the click-through are unchanged, while the PATH names the reader.
    /// </summary>
    [Fact]
    public async Task ANotification_IsDeliveredToItsAddressee_NotToTheEntitysPartition()
    {
        const string entity = "SomeSpace/Docs/spec";
        var node = await Dispatch(PlainUser, entity, "delivered-to-the-addressee")
            .Should().Within(Budget).Emit("the addressed write must land");

        Assert.Equal($"{PlainUser}/{NotificationService.SatelliteSegment}", node.Namespace);
        Assert.Equal(PlainUser, node.MainNode);
        var content = node.ContentAs<Notification>(Json);
        Assert.NotNull(content);
        Assert.Equal(PlainUser, content!.Recipient);
        // The entity survives as a REFERENCE — the bell, the panel and the React client all group
        // on TargetNodePath ?? MainNode ?? Path, so the move costs the grouping nothing.
        Assert.Equal(entity, content.TargetNodePath);
    }

    /// <summary>
    /// A dispatch with no individual recipient is addressed to the PLATFORM — it lands in
    /// <c>Admin/_Notification</c>, which is what <c>StartupErrorNotifier</c>,
    /// <c>RegistryUpdateReconciler</c>, <c>PackageUpdateReconciler</c>, <c>StaticRepoImporter</c>
    /// and the System-driven compile-failure leg all rely on.
    /// </summary>
    [Fact]
    public async Task ANotificationWithNoRecipient_IsAddressedToThePlatform()
    {
        var node = await Dispatch(null, "Plugins/Store", "addressed-to-the-platform")
            .Should().Within(Budget).Emit("a recipient-less dispatch is the platform bell");

        Assert.Equal($"{AdminPartition}/{NotificationService.SatelliteSegment}", node.Namespace);
        Assert.Equal(AdminPartition, node.MainNode);
        Assert.Equal(AdminPartition, node.ContentAs<Notification>(Json)?.Recipient);
    }

    // ── the read side (#3216), and the boundary it must not cross ─────────────────────────────

    /// <summary>
    /// 🚨 <b>THE ASSERTION THIS CHANGE SET EXISTS FOR.</b> A platform notification is readable by a
    /// platform admin and by NOBODY else. The positive half is #3216's repair (before it, the
    /// unanchored bell could not reach <c>admin.notifications</c> at any identity); the negative
    /// half is the boundary the repair must not breach.
    ///
    /// <para>The negative is non-vacuous by construction: the SAME row is read back successfully
    /// under the admin identity first, so "the plain user's snapshot does not contain it" cannot be
    /// an artefact of the write not having landed. The permission fold is asserted directly as
    /// well, so a regression is reported as a permission verdict rather than as a timing story.</para>
    /// </summary>
    [Fact]
    public async Task PlatformNotification_IsNotReadableByANonAdmin()
    {
        const string title = "Startup completed with 101 error(s)";
        var node = await Dispatch(null, AdminPartition, title)
            .Should().Within(Budget).Emit("the platform notification must be written");

        // The predicate itself: exactly one of these two identities is a platform admin.
        await Mesh.IsGlobalAdmin(PlatformAdmin).Should().Within(Budget).Match(a => a,
            "an Admin-partition grant IS the platform-admin predicate");
        await Mesh.IsGlobalAdmin(PlainUser).Should().Within(Budget).Match(a => !a,
            "owning your own partition does not make you a platform admin");

        // The fold, stated as a verdict rather than as an absence.
        await Mesh.GetEffectivePermissions(node.Path, PlatformAdmin).Should().Within(Budget)
            .Match(p => p.HasFlag(Permission.Read), "the operator must be able to read the operator bell");
        await Mesh.GetEffectivePermissions(node.Path, PlainUser).Should().Within(Budget)
            .Match(p => !p.HasFlag(Permission.Read),
                "a platform notification carries startup errors and stuck-instance detail — "
                + "leaking it to every user is a WORSE outcome than the admin seeing nothing");

        // And through the bell query the shell actually issues. Admin first — the positive control
        // that makes the negative below meaningful.
        Access.SetContext(Identity(PlatformAdmin));
        await Workspace.GetQuery("platform-bell", NotificationService.BellQuery(AdminPartition))
            .Should().Within(Budget)
            .Match(nodes => (nodes ?? []).Any(n => n.Path == node.Path),
                "#3216: the platform admin's anchored read is what finally delivers these");

        // The same query, the same row, a different identity: RLS must return nothing.
        Access.SetContext(Identity(PlainUser));
        var seenByPlainUser = await Workspace
            .GetQuery("platform-bell", NotificationService.BellQuery(AdminPartition))
            .Should().Within(Budget).Emit("the query itself must answer, so the emptiness is a VERDICT");
        Assert.DoesNotContain(seenByPlainUser, n => n.Path == node.Path);
    }

    /// <summary>
    /// 🚨 A recipient given as a PATH resolves to its partition for EVERY channel, not just for
    /// delivery: the bell lands in <c>{partition}/_Notification</c>, and the preferences read and
    /// the mailbox used are that partition's — not a lookup at
    /// <c>{partition}/Documents/spec/_NotificationSettings</c>, which would find nothing and
    /// silently default. One resolved addressee, used by both channels.
    /// </summary>
    [Fact]
    public async Task APathShapedRecipient_ResolvesToThePersonForDeliveryAndPreferences()
    {
        var node = await Dispatch($"{PlainUser}/Documents/spec", "SomeSpace/Docs/spec", "path-shaped-recipient")
            .Should().Within(Budget).Emit("a path-shaped recipient must still reach the person");

        Assert.Equal($"{PlainUser}/{NotificationService.SatelliteSegment}", node.Namespace);
        Assert.Equal(PlainUser, node.ContentAs<Notification>(Json)?.Recipient);
    }

    /// <summary>
    /// The other half of the boundary: one user's bell is not another's. Anchoring the bell to the
    /// viewer's partition is what makes this structural rather than a filter that has to hold.
    /// </summary>
    [Fact]
    public async Task OneUsersBell_IsNotAnothersBell()
    {
        var mine = await Dispatch(PlainUser, "SomeSpace/Docs/spec", "for-plain-jane-only")
            .Should().Within(Budget).Emit("the user's own notification must land");

        Access.SetContext(Identity(PlainUser));
        await Workspace.GetQuery("own-bell", NotificationService.BellQuery(PlainUser))
            .Should().Within(Budget).Match(nodes => (nodes ?? []).Any(n => n.Path == mine.Path),
                "a user reads their own bell");

        Access.SetContext(Identity(PlatformAdmin));
        var seenByAdmin = await Workspace
            .GetQuery("own-bell", NotificationService.BellQuery(PlainUser))
            .Should().Within(Budget).Emit("the query must answer for the admin too");
        Assert.DoesNotContain(seenByAdmin, n => n.Path == mine.Path);
    }
}
