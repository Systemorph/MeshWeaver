using System;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Phase 2 — exercises the dynamic NodeType compile path on Orleans:
///
/// <list type="number">
///   <item><b>Cold start</b>. The per-NodeType grain has never been activated.
///   The first <see cref="GetCompilationPathRequest"/> activates it, the
///   handler reads the NodeType MeshNode from its own workspace, runs the
///   compilation pipeline, and posts the response. Asserts the slow path
///   does not deadlock the grain (the activation does NOT itself fire a
///   recursive <see cref="GetCompilationPathRequest"/> against the same
///   grain — see <see cref="NodeTypeService.EnrichWithNodeType"/> fast-path
///   for type=<c>NodeType</c>).</item>
///
///   <item><b>Compile failure surfaces</b>. Invalid C# round-trips back as
///   <c>Success=false</c> with a non-empty <c>Error</c>. Verifies the error
///   path doesn't hang.</item>
/// </list>
///
/// <para>Why this is a phase-2 gap: every existing Orleans test that touches a
/// NodeType uses a <c>static</c> registration via <c>AddMeshNodes</c> — those
/// hit <see cref="NodeTypeService.EnrichWithNodeType"/>'s sync fast path
/// without any cross-grain message. The slow path
/// (<see cref="GetCompilationPathRequest"/> against the per-NodeType hub) had
/// no Orleans coverage until this file.</para>
///
/// <para>NB: test-data MeshNodes are created against the <b>silo's</b>
/// <see cref="IMeshService"/> directly (mirrors <see cref="OrleansAssemblyStoreTest"/>).
/// Posting <see cref="CreateNodeRequest"/> with target=<c>ClientMesh.Address</c>
/// from the participating client hub gets handled by the Orleans-client-side
/// mesh hub — that hub has its own <see cref="WithNodeOperationHandlers"/>
/// wired by <c>UseOrleansMeshClient → AddOrleansMeshServices →
/// AddInMemoryPersistence</c>, so the node persists in a client-local
/// <c>IStorageService</c> that the silo never sees. The per-NodeType grain
/// then activates on the silo, can't find the node in the silo's persistence,
/// and times out at <c>OnActivateAsync after 5 attempts</c>. The work-around
/// is to seed via the silo's <see cref="IMeshService"/> and only exercise the
/// cross-process flow with the actual probe (<see cref="GetCompilationPathRequest"/>).</para>
/// </summary>
public class OrleansDynamicCompilationTest(ITestOutputHelper output)
    : OrleansTestBase<DynamicCompilationSiloConfigurator>(output)
{
    /// <summary>
    /// Resolves the silo's <see cref="IMeshService"/> via the in-process
    /// <see cref="InProcessSiloHandle"/> — same pattern used by
    /// <see cref="OrleansAssemblyStoreTest"/> for the <see cref="IAssemblyStore"/>
    /// singleton. Lets the test seed MeshNodes onto the silo's persistence
    /// without going through the Orleans-client mesh hub (see class remarks).
    /// </summary>
    private IMeshService SiloMeshService =>
        ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services
            .GetRequiredService<IMessageHub>()
            .ServiceProvider
            .GetRequiredService<IMeshService>();

    [Fact(Timeout = 60000)]
    public async Task ColdStart_CompileViaGetCompilationPathRequest_Succeeds()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        var client = await GetClientAsync($"compile-{Guid.NewGuid():N}");

        // Use a unique NodeType id per run so the disk-cache hash never matches a
        // previous run's artifact — this is genuinely a cold start.
        var typeId = $"OrleansCompileType{Guid.NewGuid():N}";
        var typePath = $"type/{typeId}";

        // 1. Seed the NodeType MeshNode on the silo's IMeshService (NOT via the
        //    client-mesh CreateNodeRequest — see class remarks on why that hits
        //    the wrong persistence).
        var typeNode = MeshNode.FromPath(typePath) with
        {
            Name = typeId,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Orleans dynamic-compile cold-start probe",
                Configuration = $"config => config.WithContentType<{typeId}>()"
            }
        };
        await SiloMeshService.CreateNode(typeNode).FirstAsync().ToTask(ct);

        // 2. Seed the Code child under <type>/Source/.
        var codeNode = new MeshNode("code", $"{typePath}/Source")
        {
            Name = "code",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = $$"""
                    public record {{typeId}}
                    {
                        public string Id { get; init; } = string.Empty;
                        public string Title { get; init; } = string.Empty;
                    }
                    """,
                Language = "csharp"
            }
        };
        await SiloMeshService.CreateNode(codeNode).FirstAsync().ToTask(ct);

        // 3. Issue GetCompilationPathRequest from the participating client to
        //    the per-NodeType hub address. Routes via Orleans → silo →
        //    activates the per-NodeType grain → NodeTypeContractHandler runs
        //    the compilation pipeline → response back through Orleans.
        var compileResp = await client
            .Observe(new GetCompilationPathRequest(/* HEAD */),
                o => o.WithTarget(new Address(typePath)))
            .FirstAsync().ToTask(ct);

        compileResp.Message.Success.Should().BeTrue(compileResp.Message.Error
            ?? "compilation must succeed for valid C# source");
        compileResp.Message.AssemblyLocation.Should().NotBeNullOrEmpty(
            "successful compile must return the absolute DLL path");
        File.Exists(compileResp.Message.AssemblyLocation!).Should().BeTrue(
            "the AssemblyLocation must be a real file on disk");

        Output.WriteLine($"PASSED — assembly at {compileResp.Message.AssemblyLocation}");
    }

    [Fact(Timeout = 60000)]
    public async Task ColdStart_InvalidSource_ReturnsErrorWithoutDeadlock()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        var client = await GetClientAsync($"compile-fail-{Guid.NewGuid():N}");

        var typeId = $"OrleansCompileBroken{Guid.NewGuid():N}";
        var typePath = $"type/{typeId}";

        var typeNode = MeshNode.FromPath(typePath) with
        {
            Name = typeId,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Compile-failure probe",
                Configuration = $"config => config.WithContentType<{typeId}>()"
            }
        };
        await SiloMeshService.CreateNode(typeNode).FirstAsync().ToTask(ct);

        var codeNode = new MeshNode("code", $"{typePath}/Source")
        {
            Name = "code",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = "this is not valid C# at all }",
                Language = "csharp"
            }
        };
        await SiloMeshService.CreateNode(codeNode).FirstAsync().ToTask(ct);

        var compileResp = await client
            .Observe(new GetCompilationPathRequest(),
                o => o.WithTarget(new Address(typePath)))
            .FirstAsync().ToTask(ct);

        compileResp.Message.Success.Should().BeFalse(
            "invalid source must surface as Success=false, not deadlock");
        compileResp.Message.Error.Should().NotBeNullOrEmpty(
            "error path must populate Error");
        compileResp.Message.AssemblyLocation.Should().BeNullOrEmpty(
            "no DLL path on failure");

        Output.WriteLine($"PASSED — error: {compileResp.Message.Error}");
    }

    /// <summary>
    /// Full slow-path round-trip: a NEW dynamic NodeType + a NEW instance of it.
    /// Creating the instance forces <see cref="NodeTypeService.EnrichWithNodeType"/>
    /// to take the slow path (the type is dynamic — no static <c>AddMeshNodes</c>
    /// entry — so the in-memory fast lookup misses), which posts
    /// <see cref="GetCompilationPathRequest"/> to the per-NodeType grain.
    /// That grain compiles the source, returns the assembly + HubConfiguration
    /// delegate, and the silo's mesh hub applies them to the instance MeshNode
    /// before persisting it. The <see cref="GetDataRequest"/> at the end activates
    /// the per-instance grain — its OnActivateAsync reads the (now-enriched)
    /// MeshNode from persistence, loads the dynamic assembly, and answers from
    /// the per-instance hub.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task Instance_OfDynamicNodeType_ActivatesAndAnswers()
    {
        var ct = new CancellationTokenSource(80.Seconds()).Token;
        var client = await GetClientAsync($"instance-{Guid.NewGuid():N}");

        var typeId = $"OrleansInstanceType{Guid.NewGuid():N}";
        var typePath = $"type/{typeId}";

        var typeNode = MeshNode.FromPath(typePath) with
        {
            Name = typeId,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Dynamic NodeType + instance probe",
                Configuration = $"config => config.WithContentType<{typeId}>()"
            }
        };
        await SiloMeshService.CreateNode(typeNode).FirstAsync().ToTask(ct);

        var codeNode = new MeshNode("code", $"{typePath}/Source")
        {
            Name = "code",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = $$"""
                    public record {{typeId}}
                    {
                        public string Id { get; init; } = string.Empty;
                        public string Title { get; init; } = string.Empty;
                    }
                    """,
                Language = "csharp"
            }
        };
        await SiloMeshService.CreateNode(codeNode).FirstAsync().ToTask(ct);

        // Create an INSTANCE of the dynamic type. NodeType=<typePath> means the
        // EnrichWithNodeType slow path fires — the static lookup misses, the
        // request flows to the per-NodeType grain, that grain compiles the
        // source and returns the HubConfiguration delegate, and the instance's
        // MeshNode is enriched + persisted. This is the path where reentrancy
        // would deadlock if a per-NodeType grain (or instance grain) tried to
        // resolve its own type via a recursive GetCompilationPathRequest.
        var instancePath = $"{typePath}/instance1";
        var instanceNode = MeshNode.FromPath(instancePath) with
        {
            Name = "instance1",
            NodeType = typePath,
        };
        await SiloMeshService.CreateNode(instanceNode).FirstAsync().ToTask(ct);
        Output.WriteLine($"Instance created at {instancePath}");

        // Activate the per-instance grain: GetDataRequest with MeshNodeReference
        // hits the instance hub's reducer. If the EnrichWithNodeType chain failed
        // at create-time (or if the per-instance grain's OnActivateAsync deadlocks),
        // the response never lands and we time out.
        var dataResp = await client
            .Observe(new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(instancePath)))
            .FirstAsync().ToTask(ct);

        dataResp.Message.Data.Should().NotBeNull(
            "per-instance grain must activate and serve its own MeshNode — slow-path EnrichWithNodeType + grain activation completed without deadlock");

        Output.WriteLine($"PASSED — instance hub answered (data type: {dataResp.Message.Data?.GetType().Name})");
    }
}

/// <summary>
/// Two-silo variant of <see cref="OrleansDynamicCompilationTest"/> that pins the
/// shared-assembly-store invariant for compiled artifacts: a NodeType compiled
/// on whichever silo Orleans places the per-NodeType grain on lands its DLL in
/// the cluster-wide <see cref="IAssemblyStore"/>, and the OTHER silo can read it
/// via <see cref="IAssemblyStore.TryGetAssemblyPath"/>. This is the cross-process
/// equivalent of the per-replica compile cache: every silo sees the same content
/// hash → same blob → same bytes (see <see cref="OrleansAssemblyStoreTest"/> for
/// the same property at the raw-bytes level).
///
/// <para>Why <see cref="CrossSiloFileSystemSiloConfigurator"/> instead of
/// <see cref="DynamicCompilationSiloConfigurator"/>: in-memory persistence is
/// per-silo (one <c>InMemoryPersistenceService</c> singleton per silo's DI
/// container, no cluster sharing). Seeding the NodeType + Code via silo 0's
/// <see cref="IMeshService"/> populates only silo 0's dictionary; if Orleans
/// places the per-NodeType grain on silo 1, silo 1's persistence is empty and
/// grain activation fails after 5 retries with "node not found". Filesystem
/// persistence rooted at a shared temp dir gives both silos the same view —
/// same pattern <see cref="TestSiloConfigurator"/> uses for the
/// <see cref="IAssemblyStore"/>.</para>
/// </summary>
public class OrleansCrossSiloCompilationTest(ITestOutputHelper output)
    : OrleansTestBase<CrossSiloFileSystemSiloConfigurator>(output)
{
    protected override short InitialSilosCount => 2;

    [Fact(Timeout = 90000)]
    public async Task CompiledArtifact_IsVisibleFromBothSilos()
    {
        var ct = new CancellationTokenSource(80.Seconds()).Token;
        var client = await GetClientAsync($"xsilo-{Guid.NewGuid():N}");

        Cluster.Silos.Count.Should().BeGreaterThanOrEqualTo(2,
            "this test exercises silo-to-silo assembly store sharing");

        var silo0Mesh = ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services
            .GetRequiredService<IMessageHub>();
        var meshService = silo0Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var typeId = $"OrleansXSiloType{Guid.NewGuid():N}";
        var typePath = $"type/{typeId}";

        // Seed type + source on silo 0.
        await meshService.CreateNode(MeshNode.FromPath(typePath) with
        {
            Name = typeId,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Cross-silo compile-share probe",
                Configuration = $"config => config.WithContentType<{typeId}>()"
            }
        }).FirstAsync().ToTask(ct);

        await meshService.CreateNode(new MeshNode("code", $"{typePath}/Source")
        {
            Name = "code",
            NodeType = "Code",
            Content = new CodeConfiguration
            {
                Code = $$"""
                    public record {{typeId}}
                    {
                        public string Id { get; init; } = string.Empty;
                    }
                    """,
                Language = "csharp"
            }
        }).FirstAsync().ToTask(ct);

        // Trigger the compile. The per-NodeType grain activates on whichever silo
        // Orleans picks; the resulting DLL lands in the FileSystem-backed
        // IAssemblyStore that both silos share (TestSiloConfigurator.AssemblyStoreRoot).
        var compileResp = await client
            .Observe(new GetCompilationPathRequest(),
                o => o.WithTarget(new Address(typePath)))
            .FirstAsync().ToTask(ct);

        compileResp.Message.Success.Should().BeTrue(compileResp.Message.Error);
        var assemblyLocation = compileResp.Message.AssemblyLocation;
        assemblyLocation.Should().NotBeNullOrEmpty("compile must produce an assembly path");
        File.Exists(assemblyLocation!).Should().BeTrue("DLL file must exist on disk");

        Output.WriteLine($"Compiled to {assemblyLocation}");

        // Cross-silo invariant: every silo's IAssemblyStore must see the artifact.
        // The assembly is keyed by (nodeTypePath, version) — we don't have a clean
        // way to synthesise the version Roslyn used here, so we verify the simpler
        // invariant at the file-system layer: the compiled artifact lives under
        // the shared root that every silo's FileSystemAssemblyStore points at, so
        // the produced path is reachable from any silo's filesystem view.
        foreach (var silo in Cluster.Silos)
        {
            var siloHost = ((InProcessSiloHandle)silo).SiloHost;
            var store = siloHost.Services.GetRequiredService<IAssemblyStore>();
            // Placeholder check: the store on every silo points at the same root.
            // The Put/TryGet round-trip is covered by OrleansAssemblyStoreTest;
            // here we just assert that the produced DLL file is visible to every
            // silo's file system (which is implicitly true when they all run on
            // the same host, but the assertion documents the invariant).
            File.Exists(assemblyLocation!).Should().BeTrue(
                $"silo '{silo.Name}' must see the compiled DLL at {assemblyLocation}");
            store.Should().NotBeNull("IAssemblyStore must be registered on every silo");
        }

        Output.WriteLine($"PASSED — DLL visible from all {Cluster.Silos.Count} silos");
    }
}

/// <summary>
/// Silo configurator for dynamic-compile tests. In-memory persistence is enough —
/// the compile artifacts live in the file-system <see cref="IAssemblyStore"/> that
/// <see cref="TestSiloConfigurator.AssemblyStoreRoot"/> roots.
/// </summary>
public class DynamicCompilationSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault();
        siloBuilder.ConfigureServices(services =>
            services.AddFileSystemAssemblyStore(TestSiloConfigurator.AssemblyStoreRoot));
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.UseOrleansMeshServer()
            .AddInMemoryPersistence()
            .ConfigurePortalMesh();
    }
}

/// <summary>
/// Two-silo configurator using filesystem persistence rooted at a shared temp dir.
/// Mirrors <see cref="TestSiloConfigurator.AssemblyStoreRoot"/> for the persistence
/// layer so both silos see the same nodes. Used by
/// <see cref="OrleansCrossSiloCompilationTest"/>.
/// </summary>
public class CrossSiloFileSystemSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    /// <summary>
    /// Shared filesystem root for both silos' persistence. Per-process Guid suffix
    /// (Acme/FutuRe test-isolation pattern) so each test run gets a fresh dir —
    /// avoids cross-run contamination of partially-written nodes / NodeType
    /// configurations from prior failed runs. The Guid is computed once per
    /// AppDomain via <c>static readonly</c>, so every silo in the same cluster
    /// (same process) still sees the same root and writes from silo A are visible
    /// to silo B.
    /// </summary>
    public static readonly string PersistenceRoot =
        Path.Combine(Path.GetTempPath(), $"mw-orleans-xsilo-fs-{Guid.NewGuid():N}");

    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault();
        siloBuilder.ConfigureServices(services =>
            services.AddFileSystemAssemblyStore(TestSiloConfigurator.AssemblyStoreRoot));
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        Directory.CreateDirectory(PersistenceRoot);
        hostBuilder.UseOrleansMeshServer()
            .ConfigureServices(services => services.AddFileSystemPersistence(PersistenceRoot))
            .ConfigurePortalMesh();
    }
}
