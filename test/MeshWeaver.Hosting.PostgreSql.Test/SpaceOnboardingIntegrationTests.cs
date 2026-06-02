using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Stage 9d — End-to-end Space + user onboarding integration. Drives
/// the same code paths the prod portal hits when:
///
/// <list type="bullet">
///   <item>a new user signs in for the first time → <see cref="UserOnboardingService"/>
///     writes {user} root + user-catalog mirror; the per-user schema is created on
///     first write via the path-routing adapter → <c>public.ensure_partition_schema</c>.</item>
///   <item>an admin creates a new Space → the Space top-level validator eagerly
///     provisions the partition (schema + mesh_nodes + satellites) via
///     <c>public.ensure_partition_schema</c> BEFORE the root write; the post-creation
///     handler grants the creator Admin. No Admin/Partition pre-write needed.</item>
/// </list>
/// </summary>
[Collection("PostgreSql")]
public class SpaceOnboardingIntegrationTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly PostgreSqlFixture _fixture = fixture;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            MaxPoolSize = 4,
            ConnectionIdleLifetime = 10
        };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
            {
                services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString);
                services.AddSingleton<UserOnboardingService>();
                return services;
            })
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType();
    }

    /// <summary>
    /// Queries <c>information_schema</c> on the fixture's base DataSource for the
    /// set of tables present in <paramref name="schema"/>. Used to assert the
    /// per-Space partition schema + its mesh_nodes / satellite tables were
    /// provisioned by the create path.
    /// </summary>
    private async Task<HashSet<string>> GetTablesAsync(string schema, CancellationToken ct)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = _fixture.DataSource.CreateCommand(
            """
            SELECT table_name FROM information_schema.tables
            WHERE table_schema = $1
            """);
        cmd.Parameters.AddWithValue(schema);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
            tables.Add(rdr.GetString(0));
        return tables;
    }

    /// <summary>
    /// 🚨 The provisioning invariant lives in a Postgres stored procedure. Assert
    /// <c>public.ensure_partition_schema(text)</c> exists (pg_proc), then call it twice
    /// for a fresh partition: the first call must create the schema + mesh_nodes +
    /// satellite tables; the second call must be a no-op (idempotent — no error).
    /// </summary>
    [Fact(Timeout = 60000)]
    public void EnsurePartitionSchema_StoredProc_Exists_AndIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        // Touch the mesh so the schema initializer has installed the proc on public.
        _ = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // (1) The function exists in the public schema.
        var procCount = ProcExistsCountAsync(ct).ToObservable()
            .Should().Within(20.Seconds()).Emit();
        procCount.Should().BeGreaterThan(0,
            "the public.ensure_partition_schema(text) stored proc must be installed by the schema initializer");

        var partition = $"pg9d_proc_{Guid.NewGuid():N}".ToLowerInvariant()[..18];

        // (2) First call provisions the schema + tables.
        CallEnsurePartitionSchemaAsync(partition, ct).ToObservable()
            .Should().Within(30.Seconds()).Emit();
        var afterFirst = GetTablesAsync(partition, ct).ToObservable()
            .Should().Within(20.Seconds()).Emit();
        afterFirst.Should().Contain("mesh_nodes",
            "the proc must create the partition's primary table");
        afterFirst.Should().Contain("access",
            "the proc must create the _Access satellite table");
        afterFirst.Should().Contain("threads",
            "the proc must create the _Thread satellite table");
        afterFirst.Should().Contain("notifications",
            "the proc must create the _Notification satellite table");

        // (3) Second call is a no-op — must not throw, tables unchanged.
        CallEnsurePartitionSchemaAsync(partition, ct).ToObservable()
            .Should().Within(30.Seconds()).Emit();
        var afterSecond = GetTablesAsync(partition, ct).ToObservable()
            .Should().Within(20.Seconds()).Emit();
        afterSecond.SetEquals(afterFirst).Should().BeTrue(
            "the second ensure_partition_schema call must be idempotent — the table set is unchanged");
    }

    private async Task<long> ProcExistsCountAsync(CancellationToken ct)
    {
        await using var cmd = _fixture.DataSource.CreateCommand(
            """
            SELECT count(*) FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'public' AND p.proname = 'ensure_partition_schema'
            """);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<int> CallEnsurePartitionSchemaAsync(string partition, CancellationToken ct)
    {
        await using var cmd = _fixture.DataSource.CreateCommand(
            "SELECT public.ensure_partition_schema($1)");
        cmd.Parameters.AddWithValue(partition);
        await cmd.ExecuteNonQueryAsync(ct);
        return 0;
    }

    /// <summary>
    /// 🚨 Regression guard (commit 77d9941ff dropped eager provisioning). Creating a
    /// top-level Space through the real <see cref="CreateNodeRequest"/> path — WITHOUT
    /// any <c>Admin/Partition</c> pre-write — must be a fool-proof server-side invariant:
    /// <list type="number">
    ///   <item>a dedicated Postgres schema (<c>{id}.mesh_nodes</c> + satellite tables)
    ///         is provisioned (by the Space validator → <c>public.ensure_partition_schema</c>);</item>
    ///   <item>the creator gets an <c>AccessAssignment</c> at <c>{id}/_Access</c> granting
    ///         Admin (and a failed grant is NOT silently swallowed);</item>
    ///   <item>a follow-up child write (<c>{id}/_Provider/SomeProvider</c>) succeeds and
    ///         reads back — i.e. no Postgres <c>42P01 relation does not exist</c>.</item>
    /// </list>
    /// This is the MCP-create shape: post a CreateNodeRequest for a brand-new top-level
    /// namespace and everything downstream must just work.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void CreateSpace_TopLevel_ProvisionsPartitionAndGrantsCreatorAdmin()
    {
        var spaceId = $"pg9d_space_{Guid.NewGuid():N}".ToLowerInvariant()[..20];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var workspace = Mesh.GetWorkspace();
        var ct = TestContext.Current.CancellationToken;

        // 1. Create the top-level Space via the real CreateNodeRequest path.
        //    No Admin/Partition pre-write — the server must provision everything.
        var created = meshService.CreateNode(new MeshNode(spaceId)
        {
            NodeType = SpaceNodeType.NodeType,
            Name = spaceId,
            State = MeshNodeState.Active,
            Content = new Space { Name = spaceId },
        }).Should().Within(45.Seconds()).Emit();
        created.Should().NotBeNull();
        string.IsNullOrEmpty(created.Namespace).Should().BeTrue(
            "a Space is a partition root — its path is just its id, so its namespace is empty");
        created.Path.Should().Be(spaceId);

        // (a) The partition schema + mesh_nodes (+ satellite tables) must exist.
        var tables = GetTablesAsync(spaceId, ct).ToObservable()
            .Should().Within(30.Seconds()).Emit();
        tables.Should().Contain("mesh_nodes",
            $"creating Space '{spaceId}' must provision its partition schema with the primary table");
        tables.Should().Contain("access",
            "the _Access satellite table must exist so the creator-admin grant can land");

        // (b) An AccessAssignment must exist at {id}/_Access granting the creator Admin.
        var grantPath = $"{spaceId}/_Access/{TestUsers.Admin.ObjectId}_Access";
        var grant = workspace.GetMeshNodeStream(grantPath)
            .Where(n => n is not null).Take(1).Timeout(20.Seconds())
            .Catch<MeshNode?, TimeoutException>(_ => Observable.Return<MeshNode?>(null))
            .Should().Within(30.Seconds()).Emit();
        grant.Should().NotBeNull(
            "the Space post-creation handler must persist a creator-admin AccessAssignment at {id}/_Access");
        grant!.NodeType.Should().Be("AccessAssignment");
        var assignment = grant.Content.Should().BeOfType<AccessAssignment>().Subject;
        assignment.AccessObject.Should().Be(TestUsers.Admin.ObjectId);
        assignment.Roles.Should().Contain(r => r.Role == Role.Admin.Id && !r.Denied,
            "the creator must be granted the Admin role on the new Space");

        // (c) A follow-up sub-write under the Space must succeed and read back —
        //     the symptom of the regression is `42P01 relation "{id}.mesh_nodes"
        //     does not exist` here.
        var providerPath = $"{spaceId}/_Provider/SomeProvider";
        var providerSaved = meshService.CreateNode(new MeshNode("SomeProvider", $"{spaceId}/_Provider")
        {
            NodeType = "Markdown",
            Name = "SomeProvider",
            State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();
        providerSaved.Path.Should().Be(providerPath);

        var providerReadBack = workspace.GetMeshNodeStream(providerPath)
            .Where(n => n is not null).Take(1).Timeout(15.Seconds())
            .Catch<MeshNode?, TimeoutException>(_ => Observable.Return<MeshNode?>(null))
            .Should().Within(30.Seconds()).Emit();
        providerReadBack.Should().NotBeNull(
            "a child write under a freshly-created Space must read back from the provisioned schema");
    }

    /// <summary>
    /// Top-level invariant: a Space IS a partition root, so creating one with a
    /// non-empty namespace must be rejected server-side with a clear error.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void CreateSpace_WithNonEmptyNamespace_IsRejected()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var spaceId = $"pg9d_nested_{Guid.NewGuid():N}".ToLowerInvariant()[..20];

        // A Space nested under another namespace is invalid — the create must
        // OnError rather than persist a non-root Space.
        var notification = meshService.CreateNode(new MeshNode(spaceId, "SomeParent")
            {
                NodeType = SpaceNodeType.NodeType,
                Name = spaceId,
                State = MeshNodeState.Active,
                Content = new Space { Name = spaceId },
            })
            .Take(1)
            .Materialize()
            .Should().Within(30.Seconds())
            .Match(n => n.Kind == System.Reactive.NotificationKind.OnError);
        notification.Exception.Should().NotBeNull(
            "creating a Space with a non-empty namespace must be rejected — a Space is always top-level");
    }

    [Fact(Timeout = 60000)]
    public void UserOnboarding_CreatesPerUserPartition()
    {
        var username = $"pg9d_user_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var onboarding = Mesh.ServiceProvider.GetRequiredService<UserOnboardingService>();

        var partitionRoot = onboarding.CreateUser(new UserOnboardingRequest(
                Username: username,
                Email: $"{username}@example.com",
                FullName: "Test User"))
            .Should().Within(45.Seconds()).Emit();

        partitionRoot.Should().NotBeNull("UserOnboardingService.CreateUser must emit the partition-root node");
        partitionRoot.Path.Should().Be(username);

        var resolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();
        var resolution = resolver.ResolvePath(username)
            .Where(r => r is not null).Take(1).Timeout(20.Seconds())
            .Catch<AddressResolution?, TimeoutException>(_ => Observable.Return<AddressResolution?>(null))
            .Should().Within(30.Seconds()).Emit();
        resolution.Should().NotBeNull(
            "post-onboarding the per-user partition must be routable");
        resolution!.Node.Should().NotBeNull();
    }

    [Fact(Timeout = 60000)]
    public void CreateSpace_Routable_NoAdminPartitionPreWrite()
    {
        // 🚨 Previously this test pre-wrote Admin/Partition/{id} to mask the
        // missing eager provisioning (the 77d9941ff regression). Dropped: the
        // fixed create path provisions the partition itself (Space validator →
        // public.ensure_partition_schema), so creating just the Space root must
        // leave it routable.
        var orgId = $"pg9d_org_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var orgRoot = meshService.CreateNode(new MeshNode(orgId)
        {
            NodeType = "Space",
            Name = orgId,
            State = MeshNodeState.Active,
            Content = new Space { Name = orgId },
        }).Should().Within(30.Seconds()).Emit();
        orgRoot.Should().NotBeNull();

        var resolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();
        var resolution = resolver.ResolvePath(orgId)
            .Where(r => r is not null).Take(1).Timeout(20.Seconds())
            .Catch<AddressResolution?, TimeoutException>(_ => Observable.Return<AddressResolution?>(null))
            .Should().Within(30.Seconds()).Emit();
        resolution.Should().NotBeNull(
            "post-create the Space partition must be routable — without any Admin/Partition pre-write");
    }

    [Fact(Timeout = 60000)]
    public void OnboardedUser_CanWriteAndReadInOwnNamespace()
    {
        var username = $"pg9d_owner_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var onboarding = Mesh.ServiceProvider.GetRequiredService<UserOnboardingService>();
        onboarding.CreateUser(new UserOnboardingRequest(
                Username: username,
                Email: $"{username}@example.com",
                FullName: "Owner"))
            .Should().Within(45.Seconds()).Emit();

        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var notePath = $"{username}/notebook/note1";
        var saved = meshService.CreateNode(new MeshNode("note1", $"{username}/notebook")
        {
            NodeType = "Markdown",
            Name = "note1",
            State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();
        saved.Path.Should().Be(notePath);

        var workspace = Mesh.GetWorkspace();
        var readBack = workspace.GetMeshNodeStream(notePath)
            .Where(n => n is not null).Take(1).Timeout(15.Seconds())
            .Catch<MeshNode?, TimeoutException>(_ => Observable.Return<MeshNode?>(null))
            .Should().Within(30.Seconds()).Emit();
        readBack.Should().NotBeNull(
            "the newly onboarded user must be able to read back their own writes");
    }

    /// <summary>
    /// #9 (restored after the Organization→Space migration): ANY authenticated user —
    /// not only a global admin — can create a top-level Space and automatically becomes
    /// its Admin. Impersonates a brand-new user with no global-admin grant.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void CreateSpace_ByNonGlobalAdmin_Succeeds_AndGrantsCreatorAdmin()
    {
        var creator = $"pg9d_anyone_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var spaceId = $"pg9d_sp_{Guid.NewGuid():N}".ToLowerInvariant()[..18];
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var workspace = Mesh.GetWorkspace();

        // Act AS a regular (non-global-admin) user.
        accessService.SetCircuitContext(new AccessContext { ObjectId = creator, Name = creator });

        var created = meshService.CreateNode(new MeshNode(spaceId)
        {
            NodeType = SpaceNodeType.NodeType,
            Name = spaceId,
            State = MeshNodeState.Active,
            Content = new Space { Name = spaceId },
        }).Should().Within(45.Seconds()).Emit();
        created.Should().NotBeNull("any authenticated user must be able to create a top-level Space");
        created.Path.Should().Be(spaceId);

        // Creator auto-becomes Admin of the Space they created.
        var grantPath = $"{spaceId}/_Access/{creator}_Access";
        var grant = workspace.GetMeshNodeStream(grantPath)
            .Where(n => n is not null).Take(1).Timeout(20.Seconds())
            .Catch<MeshNode?, TimeoutException>(_ => Observable.Return<MeshNode?>(null))
            .Should().Within(30.Seconds()).Emit();
        grant.Should().NotBeNull("the creator must be granted Admin on the Space they created");
        var assignment = grant!.Content.Should().BeOfType<AccessAssignment>().Subject;
        assignment.Roles.Should().Contain(r => r.Role == Role.Admin.Id && !r.Denied,
            "the creator's grant must carry the non-denied Admin role");
    }

    /// <summary>
    /// #10: a Space must always retain at least one administrator. Deleting the creator's
    /// (sole) Admin AccessAssignment is rejected; once a SECOND admin exists, the first
    /// can be removed. Enforced by <c>SpaceAdminInvariantValidator</c>.
    /// </summary>
    [Fact(Timeout = 90000)]
    public void Space_CannotRemoveLastAdmin_AllowedAfterSecondAdmin()
    {
        var spaceId = $"pg9d_lastadm_{Guid.NewGuid():N}".ToLowerInvariant()[..20];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var workspace = Mesh.GetWorkspace();

        meshService.CreateNode(new MeshNode(spaceId)
        {
            NodeType = SpaceNodeType.NodeType,
            Name = spaceId,
            State = MeshNodeState.Active,
            Content = new Space { Name = spaceId },
        }).Should().Within(45.Seconds()).Emit();

        var firstAdminPath = $"{spaceId}/_Access/{TestUsers.Admin.ObjectId}_Access";
        workspace.GetMeshNodeStream(firstAdminPath)
            .Where(n => n is not null).Take(1).Timeout(20.Seconds())
            .Should().Within(30.Seconds()).Emit();

        // (1) Deleting the LAST admin must be blocked (DeleteNode emits false / OnError,
        //     never OnNext(true)).
        var blocked = meshService.DeleteNode(firstAdminPath)
            .Take(1).Materialize().Should().Within(30.Seconds()).Emit();
        var deletedLast = blocked.Kind == System.Reactive.NotificationKind.OnNext && blocked.Value;
        deletedLast.Should().BeFalse("deleting the last admin of a Space must be rejected");

        // (2) Add a SECOND admin.
        const string secondAdmin = "pg9dsecondadmin";
        meshService.CreateNode(new MeshNode($"{secondAdmin}_Access", $"{spaceId}/_Access")
        {
            NodeType = "AccessAssignment",
            Name = $"{secondAdmin} Access",
            MainNode = spaceId,
            Content = new AccessAssignment
            {
                AccessObject = secondAdmin,
                DisplayName = secondAdmin,
                Roles = [new RoleAssignment { Role = Role.Admin.Id, Denied = false }],
            },
        }).Should().Within(30.Seconds()).Emit();

        // Wait until the read-side (what the validator reads) shows BOTH admins — accumulate
        // ids across Initial + delta emissions so we don't race eventual consistency.
        MeshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{spaceId}/_Access nodeType:AccessAssignment"))
            .Scan(ImmutableHashSet<string>.Empty, (acc, c) =>
                c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset
                    ? c.Items.Select(n => n.Id).ToImmutableHashSet()
                    : acc.Union(c.Items.Select(n => n.Id)))
            .Where(ids => ids.Count >= 2)
            .Take(1).Timeout(30.Seconds())
            .Should().Within(35.Seconds()).Emit();

        // (3) Now the first admin CAN be removed — a second admin remains.
        var allowed = meshService.DeleteNode(firstAdminPath)
            .Take(1).Materialize().Should().Within(30.Seconds()).Emit();
        var deletedFirst = allowed.Kind == System.Reactive.NotificationKind.OnNext && allowed.Value;
        deletedFirst.Should().BeTrue("with a second admin present, the first admin can be removed");
    }
}
