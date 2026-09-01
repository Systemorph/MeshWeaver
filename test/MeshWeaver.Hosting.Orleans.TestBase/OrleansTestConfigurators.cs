using System;
using System.IO;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Documentation;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.TestingHost;

using MeshWeaver.Compiler;
namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Mirrors the silo's mesh-builder chain (<see cref="OrleansTestMeshExtensions.ConfigurePortalMesh"/>)
/// on the Orleans client so the client-side mesh catalog has the same NodeType
/// registrations (Graph, AI, Kernel). Without this, <c>CreateNodeRequest</c>
/// posted to the client mesh address fails with "NodeType '&lt;X&gt;' is not
/// registered" because the local catalog is empty.
/// </summary>
public class TestClientConfigurator : IHostConfigurator
{
    public void Configure(IHostBuilder hostBuilder)
    {
        // 🚨 The in-memory backing store this client shares with the silo is a PER-CLUSTER
        // instance, so it cannot be registered here — Orleans instantiates this configurator
        // via new(). The deploying fixture passes it in through
        // OrleansTestCluster.DeployAsync's post-configure closure; see OrleansTestBackingStore.
        hostBuilder.UseOrleansMeshClient()
            .ConfigurePortalMesh(MeshExtra);
    }

    /// <summary>
    /// Subclass hook: mesh registrations for the CLIENT mesh.
    ///
    /// <para>🚨 This is a SEPARATE player from the silo. The client mesh hub (<c>mesh/{id}</c>) is
    /// what <c>Fixture.ClientMesh.Address</c> resolves to, and tests post <c>CreateNodeRequest</c>
    /// straight at it — so a node type registered only on the SILO is refused here with
    /// "NodeType 'X' is not registered". Wiring the silo and forgetting this reads as a missing
    /// AddX() on the silo and is not (measured: 2 tests, 2026-08-28).</para>
    /// </summary>
    protected virtual Func<MeshBuilder, MeshBuilder>? MeshExtra => null;
}

/// <summary>
/// Canonical silo configurator. Inherit and override <see cref="ConfigureMesh"/>
/// to add per-test seeds, or <see cref="RegisterChatClientFactory"/> to plug in a
/// fake chat-client factory. The base wires:
///
/// <list type="bullet">
///   <item><c>PersistenceExtensions.AddPartitionedInMemoryPersistence(MeshBuilder)</c>
///   so <see cref="IPartitionStorageProvider"/> rules (e.g.
///   <see cref="EmbeddedResourcePartitionStorageProvider"/> registered by
///   <see cref="DocumentationExtensions.AddDocumentation"/>) actually serve reads.
///   See <c>Doc/Architecture/PartitionedPersistence.md</c>.</item>
///   <item><see cref="OrleansTestMeshExtensions.ConfigurePortalMesh"/>: <c>AddGraph</c>,
///   <c>AddAI</c>, <c>AddKernel</c>, plus the test assembly's <c>HubFactory</c>
///   and <c>Kernel</c> NodeType registrations.</item>
///   <item><see cref="DocumentationExtensions.AddDocumentation"/>: registers the
///   <c>Doc</c> embedded-resource partition.</item>
///   <item><c>SecurityHostingExtensions.AddRowLevelSecurity</c>:
///   ScopeRolesService + SecurityService. Combined with the <c>TestUser</c>
///   admin seeds below, every test starts with a logged-in admin user.</item>
///   <item>TestUser admin seeds: <c>User/TestUser</c> + <c>User/_Access/TestUser_Access</c>
///   so the default identity has Admin role.</item>
///   <item><c>MeshHubBuilderExtensions.ConfigureDefaultNodeHub</c> with
///   <c>LayoutExtensions.AddDefaultLayoutAreas</c>.</item>
///   <item>Per-process Guid-suffixed <see cref="IAssemblyStore"/> root (Acme/FutuRe
///   isolation pattern).</item>
///   <item>Silo-side framework logging through <see cref="ITestOutputHelper"/> so
///   silo errors aren't lost on a crash.</item>
/// </list>
///
/// <para>With this baseline, the typical per-test configurator only needs to
/// override <see cref="RegisterChatClientFactory"/> to plug in a fake AI
/// factory.</para>
/// </summary>
public class TestSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    /// <summary>
    /// Shared root directory for the <see cref="IAssemblyStore"/> across every silo in
    /// the test cluster. Per-process Guid suffix (mirrors the Acme/FutuRe test
    /// isolation pattern) so a stale DLL from a previous test process can't collide
    /// on Windows file locks. The Guid is computed once per AppDomain, so every silo
    /// in the same cluster sees the same root and the cross-silo Put-on-A /
    /// TryGet-on-B invariant holds.
    /// </summary>
    public static readonly string AssemblyStoreRoot =
        Path.Combine(Path.GetTempPath(), $"mw-orleans-asmstore-{Guid.NewGuid():N}");

    /// <summary>
    /// Subclass hook: register extra silo-side services. Default is
    /// no-op; tests that need agent behaviour register their fake here.
    /// </summary>
    protected virtual void RegisterChatClientFactory(IServiceCollection services) { }

    /// <summary>
    /// Subclass hook: add per-test mesh nodes / seeds / extensions. Called after
    /// the canonical chain, so seeds layer on top of the standard config.
    /// </summary>
    protected virtual MeshBuilder ConfigureMesh(MeshBuilder builder) => builder;

    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault()
            // Surface silo-side framework logs through ITestOutputHelper. Without
            // this, silo errors vanish on a test crash leaving only the test's own
            // Output.WriteLine, which makes hangs / stack overflows diagnostically
            // opaque in CI.
            .ConfigureLogging(logging => logging.AddXUnitLogger());
        siloBuilder.ConfigureServices(services =>
            services.AddFileSystemAssemblyStore(AssemblyStoreRoot));
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        // 🚨 The in-memory backing store this silo shares with the Orleans client
        // (TestClientConfigurator) is a PER-CLUSTER instance and cannot be registered here:
        // Orleans instantiates this configurator via new(). The deploying fixture passes it
        // in through OrleansTestCluster.DeployAsync's post-configure closure. The single-process
        // test cluster mirrors prod's "multiple adapter instances, same PG backend" shape so a
        // node created via either mesh hub is visible to the silo's path resolver.
        var meshBuilder = hostBuilder.UseOrleansMeshServer()
            .AddPartitionedInMemoryPersistence()
            .ConfigurePortalMesh()
            .AddDocumentation()
            .AddRowLevelSecurity()
            .AddMeshNodes(new MeshNode("TestUser", "User") { Name = "TestUser", NodeType = "User" })
            .AddMeshNodes(TestUserAdminAccess())
            .ConfigureServices(services =>
            {
                RegisterChatClientFactory(services);
                return services;
            })
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());

        ConfigureMesh(meshBuilder);
    }

    /// <summary>
    /// TestUser-specific Admin seed (mirrors
    /// <c>samples/Graph/Data/User/_Access/TestUser_Access.json</c>). Namespace MUST
    /// end in <c>/_Access</c> — see <c>SecurityService.ComputeScopeRoles</c>; anything
    /// else is silently dropped, leaving the user with zero permissions.
    /// </summary>
    private static MeshNode[] TestUserAdminAccess()
    {
        var assignment = new AccessAssignment
        {
            AccessObject = "TestUser",
            DisplayName = "Test User",
            Roles = [new RoleAssignment { Role = "Admin" }]
        };
        return [new("TestUser_Access", "User/_Access")
        {
            NodeType = "AccessAssignment",
            Name = "TestUser Access",
            Content = assignment,
            MainNode = "User",
        }];
    }
}
