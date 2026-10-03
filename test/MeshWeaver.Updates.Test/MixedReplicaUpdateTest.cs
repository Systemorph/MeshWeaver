using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Compiler;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Hosting.Orleans.Test;
using MeshWeaver.Hosting.Persistence;
using Orleans.Hosting;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Updates.Test;

/// <summary>
/// Scenario 7 — MIXED BUILDS DURING A ROLL, on a two-silo Orleans cluster sharing one store: an
/// activation bound to N and an activation bound to N+1 coexist, each answers its own requests
/// correctly, and nothing assumes the update happened atomically. Then the N activation is disposed
/// and re-binds N+1, while the N+1 activation is untouched.
///
/// <para>Grain placement is Orleans' choice, so this does not pin WHICH silo hosts which build — it
/// pins the property a roll depends on: two builds of one type serving side by side in one
/// cluster, with every read answered by the build its activation bound.</para>
/// <para>Falsified by any "re-bind every activation on publish" shortcut (a publish-driven global
/// swap): the N activation would answer N1 before its dispose.</para>
/// </summary>
public class MixedReplicaUpdateTest(ITestOutputHelper output) : OrleansMeshTestBase(output)
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    protected override Type SiloConfiguratorType => typeof(SharedStoreSiloConfigurator);

    /// <inheritdoc />
    protected override IMeshBootstrap Bootstrap => MeshBootstrap.Orleans(o => o.WithSilos(2));

    private IMessageHub SiloMesh(int index) =>
        ((InProcessSiloHandle)Cluster.Silos[index]).SiloHost.Services.GetRequiredService<IMessageHub>();

    private IMeshService SiloMeshService => SiloMesh(0).ServiceProvider.GetRequiredService<IMeshService>();

    private static string Code(string marker) => $$"""
        using MeshWeaver.Layout.Composition;
        public static class UpdateProbeAreas
        {
            public static UiControl Overview(LayoutAreaHost host, RenderingContext _)
                => Controls.Html("<div id='marker'>MARKER_{{marker}}</div>");
        }
        """;

    /// <summary>Waits until EVERY silo's mesh hub sees the type in the state
    /// <paramref name="predicate"/> describes — an activation reads the type through its own silo's
    /// mirror, so one silo's view is not evidence about the other's.</summary>
    private async Task<NodeTypeDefinition> OnEverySilo(string typePath, Func<NodeTypeDefinition, bool> predicate, string because)
    {
        NodeTypeDefinition? seen = null;
        for (var i = 0; i < Cluster.Silos.Count; i++)
        {
            var mesh = SiloMesh(i);
            seen = await mesh.GetWorkspace().GetMeshNodeStream(typePath)
                .Select(n => n?.ContentAs<NodeTypeDefinition>(mesh.JsonSerializerOptions))
                .Where(d => d is not null && predicate(d))
                .Select(d => d!)
                .Take(1)
                .Should().Within(Step).Emit($"silo {i}: {because}", cancellationToken: TestContext.Current.CancellationToken);
        }
        return seen!;
    }

    private Task Release(IMessageHub client, string typePath)
        => client.Observe(new CreateReleaseRequest(Force: false), o => o.WithTarget(new Address(typePath)))
            .Take(1)
            .Should().Within(Step).Emit("the release request must be answered",
                cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>What <paramref name="instancePath"/> serves now, read through a FRESH client — a
    /// client's workspace keeps its remote stream per (address, area), so reusing one would replay
    /// the snapshot it took from an activation that has since been disposed.</summary>
    private Task<string> Served(string instancePath)
    {
        var reference = new LayoutAreaReference("Overview");
        return GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(instancePath), reference)
            .GetControlStream(reference.Area!)
            .OfType<HtmlControl>()
            .Select(h => h.Data?.ToString() ?? string.Empty)
            .Where(html => html.Contains("MARKER_", StringComparison.Ordinal))
            .Take(1)
            .Should().Within(Step).Emit($"'{instancePath}' must serve a build",
                cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 300_000)]
    public async Task TwoBuildsServeSideBySide_AndADisposeMovesOnlyItsOwnActivation()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        Cluster.Silos.Count.Should().Be(2, "a roll is two generations of replicas");
        var client = GetClient(Guid.NewGuid().ToString("N")[..12]);

        var id = $"MixedProbe{Guid.NewGuid():N}"[..20];
        var typePath = $"type/{id}";
        await SiloMeshService.CreateNode(MeshNode.FromPath(typePath) with
        {
            Name = id,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", UpdateProbeAreas.Overview))",
            },
        }).Take(1).Should().Within(Step).Emit(cancellationToken: TestContext.Current.CancellationToken);
        await SiloMeshService.CreateNode(new MeshNode("code", $"{typePath}/Source")
        {
            Name = "code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = Code("N"), Language = "csharp" },
        }).Take(1).Should().Within(Step).Emit(cancellationToken: TestContext.Current.CancellationToken);

        await Release(client, typePath);
        var n = await OnEverySilo(typePath,
            d => d.CompilationStatus == CompilationStatus.Ok && !string.IsNullOrEmpty(d.LatestAssemblyPath),
            "build N is published");

        var onN = $"{typePath}/on-n";
        await SiloMeshService.CreateNode(MeshNode.FromPath(onN) with { Name = "on-n", NodeType = typePath })
            .Take(1).Should().Within(Step).Emit(cancellationToken: TestContext.Current.CancellationToken);
        (await Served(onN)).Should().Contain("MARKER_N<", "the first activation binds N");

        // Publish N+1 while the N activation is live.
        var source = await SiloMesh(0).GetWorkspace().GetMeshNodeStream($"{typePath}/Source/code")
            .Where(x => x is not null).Take(1)
            .Should().Within(Step).Emit(cancellationToken: TestContext.Current.CancellationToken);
        await SiloMeshService.UpdateNode(source! with
        {
            Content = new CodeConfiguration { Code = Code("N1"), Language = "csharp" },
        }).Take(1).Should().Within(Step).Emit(cancellationToken: TestContext.Current.CancellationToken);
        await OnEverySilo(typePath, d => d.IsDirty, "the edit reached the type");
        await Release(client, typePath);
        await OnEverySilo(typePath,
            d => d.CompilationStatus == CompilationStatus.Ok
                 && !string.Equals(d.LatestAssemblyPath, n.LatestAssemblyPath, StringComparison.Ordinal),
            "build N+1 is published");

        var onN1 = $"{typePath}/on-n1";
        await SiloMeshService.CreateNode(MeshNode.FromPath(onN1) with { Name = "on-n1", NodeType = typePath })
            .Take(1).Should().Within(Step).Emit(cancellationToken: TestContext.Current.CancellationToken);

        // Both builds serve at once, each from its own activation.
        (await Served(onN1)).Should().Contain("MARKER_N1<", "a fresh activation binds N+1");
        (await Served(onN)).Should().Contain("MARKER_N<",
            "the N activation keeps serving N — no atomic switch, no shared state between builds");
        (await Served(onN1)).Should().Contain("MARKER_N1<", "and the N+1 one keeps serving N+1");

        // The dispose moves exactly the activation it reaches.
        await client.RecycleNode(onN, Step, reason: "Updates suite: re-bind the N activation")
            .Take(1).Should().Within(Step + Step).Emit(cancellationToken: TestContext.Current.CancellationToken);
        (await Served(onN)).Should().Contain("MARKER_N1<", "after its dispose it binds N+1");
        (await Served(onN1)).Should().Contain("MARKER_N1<", "the other activation is untouched");
    }
}

/// <summary>
/// Two silos over ONE durable store and ONE assembly store — the shape of a roll's two replica
/// generations, both reading the same partitions and the same compiled builds. Filesystem
/// persistence rooted per process (both silos of the cluster share it), the same pattern as
/// <c>OrleansCrossSiloCompilationTest</c>.
/// </summary>
public class SharedStoreSiloConfigurator : Orleans.TestingHost.ISiloConfigurator, Orleans.TestingHost.IHostConfigurator
{
    /// <summary>Per-process persistence root shared by every silo of the cluster.</summary>
    public static readonly string PersistenceRoot =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mw-updates-xsilo-{Guid.NewGuid():N}");

    /// <inheritdoc />
    public void Configure(Orleans.Hosting.ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault();
        siloBuilder.ConfigureServices(services =>
            services.AddFileSystemAssemblyStore(TestSiloConfigurator.AssemblyStoreRoot));
    }

    /// <inheritdoc />
    public void Configure(Microsoft.Extensions.Hosting.IHostBuilder hostBuilder)
    {
        System.IO.Directory.CreateDirectory(PersistenceRoot);
        hostBuilder.UseOrleansMeshServer()
            .ConfigureServices(services => services.AddFileSystemPersistence(PersistenceRoot))
            .ConfigurePortalMesh();
    }
}
