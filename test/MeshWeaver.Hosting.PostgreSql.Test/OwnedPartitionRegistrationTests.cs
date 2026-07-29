using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Does provisioning a partition-owning node also REGISTER the partition at
/// <c>Admin/Partition/{name}</c>?
///
/// <para>This has to run against Postgres. The in-memory test base routes by path regardless of
/// what the registry holds, so a Monolith-only version of these assertions passes whether or not
/// the registration happens — it cannot see the difference and would certify either behaviour.</para>
///
/// <para><b>Where the question came from.</b> On the education e2e mesh the schema
/// <c>"e2e-admin"</c> existed and held the user's root node, <c>auth.mesh_nodes</c> mirrored it —
/// and <c>Admin/Partition</c> listed eleven entries, every one a package or space and not one user.
/// Reads inside that partition hung to the full <c>SubscribeRequest</c> timeout and the install page
/// died blank after 3.5 minutes.</para>
///
/// <para><b>What is deliberately NOT assumed here.</b> That the missing registration is the cause.
/// <c>PostgreSqlPartitionSubscriptionHostedService</c> documents the opposite — "the router maps a
/// path's first segment to a schema synchronously", and <c>SpaceOnboardingIntegrationTests</c> states
/// "No Admin/Partition pre-write needed". So these tests ask the narrow, checkable question — is the
/// partition registered, and does an address inside it resolve — and let the answer stand. A test
/// written to confirm a hypothesis is how you certify a fix that does nothing.</para>
/// </summary>
[Collection("PostgreSql")]
public class OwnedPartitionRegistrationTests(PostgreSqlFixture fixture, ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly PostgreSqlFixture _fixture = fixture;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var csb = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            MaxPoolSize = 16,
            ConnectionIdleLifetime = 10
        };
        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
            {
                services.AddPartitionedPostgreSqlPersistence(csb.ConnectionString);
                return services;
            })
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType()
            .AddUserType();
    }

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private static string DefinitionPath(string partition) =>
        $"{PartitionNodeType.Namespace}/{partition}";

    /// <summary>A fresh, lower-case partition id — the schema is the lower-cased namespace.</summary>
    private static string NewPartition() => $"regtest{Guid.NewGuid():N}"[..14];

    private Task<MeshNode> CreateOwner(string partition, string nodeType) =>
        MeshService.CreateNode(MeshNode.FromPath(partition) with
        {
            NodeType = nodeType,
            Name = partition,
            State = MeshNodeState.Active,
        }).FirstAsync().ToTask();

    /// <summary>
    /// 🚨 THE QUESTION. After a partition-owning create, is the partition registered?
    ///
    /// <para>If this fails, a self-provisioned partition is invisible to every consumer that reads
    /// the registry rather than the path — the partitions settings tab, the completion
    /// orchestrator's <c>namespace:Admin/Partition nodeType:Partition</c> query,
    /// <c>PartitionRegistry</c>'s own lookup — while package-installed partitions, which
    /// <c>PackageInstaller</c> registers explicitly, are listed. That asymmetry is what made this
    /// worth checking: the paths people exercise most are the registered ones.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task CreatingAnOwningNode_RegistersThePartition()
    {
        var partition = NewPartition();

        await CreateOwner(partition, "Space");

        var definition = await Storage.Read(DefinitionPath(partition), Mesh.JsonSerializerOptions)
            .Should().Within(30.Seconds()).Emit();

        definition.Should().NotBeNull(
            $"creating a partition-owning node should register '{partition}' at "
            + $"{DefinitionPath(partition)} — otherwise the partition exists but is not discoverable "
            + "through the registry every catalog and settings surface reads");
        definition!.NodeType.Should().Be(PartitionNodeType.NodeType);
    }

    /// <summary>
    /// The registration must describe the partition it is for. A definition pointing at the wrong
    /// schema is worse than none: reads and writes would resolve into another partition's data
    /// rather than failing.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheRegistration_DescribesThatPartition()
    {
        var partition = NewPartition();

        await CreateOwner(partition, "Space");
        var definition = await Storage.Read(DefinitionPath(partition), Mesh.JsonSerializerOptions)
            .Should().Within(30.Seconds()).Emit();
        definition.Should().NotBeNull();

        var content = definition!.ContentAs<PartitionDefinition>(Mesh.JsonSerializerOptions);
        content.Should().NotBeNull("the definition must carry a PartitionDefinition");
        content!.Namespace.Should().Be(partition);
        content.Schema.Should().Be(partition.ToLowerInvariant(),
            "provisioning and routing must agree on the schema name");
    }

    /// <summary>
    /// THE PROPERTY THAT ACTUALLY MATTERS, independent of how it is achieved: a node written into a
    /// freshly self-provisioned partition must be readable back promptly.
    ///
    /// <para>This is the assertion that speaks to the reported symptom. The failure mode there was
    /// never an error — it was a read that never returned, so this is bounded deliberately: an
    /// unbounded await would hang the suite exactly the way it hung the page, and report as a
    /// timeout kill rather than a diagnosis.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ANodeInsideAFreshPartition_IsReadableBack()
    {
        var partition = NewPartition();
        await CreateOwner(partition, "Space");

        var child = MeshNode.FromPath($"{partition}/Probe") with
        {
            NodeType = "Markdown",
            Name = "Probe",
            State = MeshNodeState.Active,
        };
        await MeshService.CreateNode(child).FirstAsync().ToTask();

        var read = await Storage.Read(child.Path, Mesh.JsonSerializerOptions)
            .Should().Within(30.Seconds()).Emit();

        read.Should().NotBeNull(
            $"a node written into the freshly provisioned partition '{partition}' must be readable "
            + "back — if this times out, the partition is provisioned but not usable, which is the "
            + "shape that presents as a blank page rather than an error");
    }

    /// <summary>
    /// Provisioning is re-entered on every top-level create of an owning type, so a second create
    /// for the same partition must leave it registered rather than failing or clearing it.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task RegisteringTwice_IsHarmless()
    {
        var partition = NewPartition();

        await CreateOwner(partition, "Space");
        var first = await Storage.Read(DefinitionPath(partition), Mesh.JsonSerializerOptions)
            .Should().Within(30.Seconds()).Emit();
        first.Should().NotBeNull();

        try { await CreateOwner(partition, "Space"); }
        catch { /* a duplicate create may be refused; the registration must survive either way */ }

        var second = await Storage.Read(DefinitionPath(partition), Mesh.JsonSerializerOptions)
            .Should().Within(30.Seconds()).Emit();
        second.Should().NotBeNull("re-provisioning must not remove the registration");
    }
    /// <summary>CONTROL: a path that was never created must read back as null.</summary>
    [Fact(Timeout = 120_000)]
    public async Task Control_AnUncreatedPath_ReadsBackNull()
    {
        var ghost = await Storage.Read($"{PartitionNodeType.Namespace}/never{Guid.NewGuid():N}", Mesh.JsonSerializerOptions)
            .Should().Within(30.Seconds()).Emit();
        ghost.Should().BeNull("if this is non-null, every NotBeNull assertion in this class is vacuous");
    }

    /// <summary>
    /// WHERE THE USER PATH ACTUALLY DIVERGES — the finding this class exists to record.
    ///
    /// <para>A top-level <c>User</c> cannot be created through the ordinary create pipeline at all:
    /// it comes back <c>Access denied: Create permission required</c>. Users are onboarded by the
    /// portal writing the root <b>straight through the storage adapter</b> (see
    /// <c>FirstUserOnboardingTests</c>, which does exactly that), so the create-validation chain —
    /// and therefore <c>OwnsPartitionProvisioningValidator</c>, where partition provisioning lives —
    /// never runs for a user.</para>
    ///
    /// <para>That, not a missing line in the validator, is why the education e2e mesh had eleven
    /// packages and spaces in <c>Admin/Partition</c> and not one user: the user's SCHEMA is created
    /// (ensure_partition_schema, via the adapter) while nothing on that path registers the
    /// partition. Any fix belongs in onboarding, and this test pins the constraint so the next
    /// attempt does not start where the last one did — in the validator, which a user create never
    /// reaches.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AUserRootCannotBeCreatedThroughTheCreatePipeline()
    {
        var partition = NewPartition();

        var attempt = async () => await CreateOwner(partition, "User");

        await attempt.Should().ThrowAsync<UnauthorizedAccessException>(
            "users are onboarded by writing through the storage adapter, not via CreateNode — so "
            + "the create-validation chain never sees a user, and nothing there can register the "
            + "user's partition");
    }
}
