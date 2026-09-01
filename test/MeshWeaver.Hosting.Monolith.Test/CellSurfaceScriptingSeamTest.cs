using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Kernel;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The pack-scripting seam's acceptance tests (issue #1649) — the scenario that was structurally
/// impossible before: a kernel <c>--render</c>/executable cell calling a DYNAMIC NodeType's
/// Source API by bare name, deterministically.
///
/// <list type="bullet">
///   <item><b>Modules</b> join the per-session assembly set through their
///     <see cref="InstalledModuleAssembly"/> DI registrations (part 1).</item>
///   <item>A NodeType that declares <c>cellSurface: true</c> is compiled, its CURRENT baked
///     assembly joins the session's reference set, and a cell calling its Source class by bare
///     name COMPILES AND EXECUTES — the runtime bind crosses from the session's collectible
///     load context into the node assembly's collectible context (parts 2 + 4).</item>
///   <item>A NodeType WITHOUT the flag stays invisible to cells — by construction, not by
///     load-order luck: collectible-context assemblies never enter the frozen snapshot, so the
///     cell fails with CS0103 regardless of what happened to load first.</item>
///   <item>A <c>shared=</c> consumer of a cell-surface type's Source fails ITS compile with a
///     message naming the owner — the CS0433 duplicate-copy class prevented by construction
///     (part 3).</item>
/// </list>
/// </summary>
public class CellSurfaceScriptingSeamTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — each fact uses its own NodeType + kernel session.</summary>
    protected override bool ShareMeshAcrossTests => true;

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    [Fact]
    public void InstalledModules_JoinTheSessionAssemblySet()
    {
        // Part 1 (unit): the per-session assembly set enumerates the mesh's
        // InstalledModuleAssembly registrations (#1653) — immune to the frozen snapshot.
        var moduleAssembly = typeof(GraphConfigurationExtensions).Assembly;

        var without = new ServiceCollection().BuildServiceProvider();
        MeshScriptEnvironment.SessionAssemblies(without)
            .Should().NotContain(moduleAssembly, "nothing registered this assembly");

        var with = new ServiceCollection()
            .AddSingleton(new InstalledModuleAssembly(moduleAssembly))
            .BuildServiceProvider();
        MeshScriptEnvironment.SessionAssemblies(with)
            .Should().Contain(moduleAssembly, "a boot-installed module must be cell-visible per session");
    }

    /// <summary>
    /// Creates the NodeType + its Source Code node(s), then observes the compile settle on the
    /// NodeType's own stream (the first-build kickoff drives Roslyn). Same shape as
    /// <c>CompileActivityLogTest.CreateAndCompile</c>.
    /// </summary>
    private async Task<NodeTypeDefinition> CreateAndCompile(
        string nodeTypePath,
        NodeTypeDefinition definition,
        params (string Name, string Code)[] sources)
    {
        var typeNode = MeshNode.FromPath(nodeTypePath) with
        {
            Name = nodeTypePath.Split('/').Last(),
            NodeType = MeshNode.NodeTypePath,
            Content = definition,
            State = MeshNodeState.Active
        };

        await MeshService.CreateNode(typeNode)
            .SelectMany(_ => sources
                .Select(source => MeshService.CreateNode(new MeshNode(source.Name, $"{nodeTypePath}/Source")
                {
                    NodeType = "Code",
                    Name = source.Name,
                    Content = new CodeConfiguration { Code = source.Code, Language = "csharp" },
                    State = MeshNodeState.Active
                }))
                .Aggregate(Observable.Return<MeshNode?>(null), (chain, next) =>
                    chain.SelectMany(_ => next.Select(n => (MeshNode?)n))))
            .Should().Within(30.Seconds()).Emit();

        var node = await Mesh.GetMeshNodeStream(nodeTypePath)
            .Should().Within(60.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error);
        return (NodeTypeDefinition)node.Content!;
    }

    /// <summary>Creates a kernel (Activity node) and returns its address + the observing client —
    /// the same submission surface every executable cell uses (<c>SubmitCodeRequest</c>).</summary>
    private async Task<(Address KernelAddress, IMessageHub Client)> CreateKernel(string marker)
    {
        const string ownerPath = "rbuergi";
        var activityNamespace = $"{ownerPath}/_Activity";
        var id = $"{marker}-{Guid.NewGuid():N}";
        var activityNode = new MeshNode(id, activityNamespace)
        {
            Name = "cell-surface seam probe",
            NodeType = "Activity",
            MainNode = ownerPath,
            State = MeshNodeState.Active,
            Content = new ActivityLog("KernelExecution") { Status = ActivityStatus.Running }
        };
        await MeshService.CreateNode(activityNode).Should().Within(60.Seconds()).Emit();
        return (new Address($"{activityNamespace}/{id}"), GetClient());
    }

    private IObservable<ActivityLog> LogWith(IMessageHub client, Address kernelAddress, string marker)
        => client.GetWorkspace()
            .GetMeshNodeStream(kernelAddress.Path)
            .Select(change => change?.Content as ActivityLog)
            .Where(log => log is not null && log!.Messages.Any(m => m.Message.Contains(marker)))!;

    [Fact(Timeout = 120_000)]
    public async Task CellSurfacePackType_IsCallableByBareName_FromAKernelCell()
    {
        // Parts 2 + 4 — the previously-impossible scenario: compile a DYNAMIC NodeType that
        // opts into the cell surface, then call its Source class by bare name from a cell.
        var def = await CreateAndCompile(
            "type/CellPack",
            new NodeTypeDefinition
            {
                CellSurface = true,
                Configuration = "config => config.WithContentType<CellPackContent>()"
            },
            ("api", """
                public record CellPackContent { public string Title { get; init; } = ""; }
                public static class CellPackApi { public static int TheAnswer() => 42; }
                """));
        def.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"the pack type must compile; error: {def.CompilationError}");

        var (kernelAddress, client) = await CreateKernel("cellpack");

        // Bare-name call: compile-time visibility comes from the session's cell-surface
        // reference; EXECUTION requires the runtime bind into the node assembly's collectible
        // load context — the part the issue flagged as unproven.
        client.Post(
            new SubmitCodeRequest("""Console.WriteLine($"cellsurface-{CellPackApi.TheAnswer()}");"""),
            o => o.WithTarget(kernelAddress));
        await LogWith(client, kernelAddress, "cellsurface-42").Should().Within(60.Seconds()).Emit();
    }

    [Fact(Timeout = 180_000)]
    public async Task ACellSurfaceTypeWhoseBuildIsProvenStale_IsNotJoined()
    {
        // 🚨 #2820, enforcement site 2 — the one the per-instance-hub gate cannot reach. The cell
        // surface loads the assembly straight through NodeAssemblyLoadContext, with no enrichment
        // and no HubConfiguration, and from that moment every submission in the session can call
        // the pack's functions by bare name with full write access. It is the most directly ARMED
        // surface there is.
        //
        // The comparison is what makes this mean something: the FIRST submission proves the type
        // is genuinely on the cell surface (identical to
        // CellSurfacePackType_IsCallableByBareName_FromAKernelCell), and the second differs from
        // it in exactly one field of the NodeType node — BuildProvenance. Asserting only the
        // refusal would pass just as well against a provider that had stopped joining anything.
        var def = await CreateAndCompile(
            "type/StalePack",
            new NodeTypeDefinition
            {
                CellSurface = true,
                Configuration = "config => config.WithContentType<StalePackContent>()"
            },
            ("api", """
                public record StalePackContent { public string Title { get; init; } = ""; }
                public static class StalePackApi { public static int TheAnswer() => 7; }
                """));
        def.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"the pack type must compile; error: {def.CompilationError}");

        const string call = """Console.WriteLine($"stalepack-{StalePackApi.TheAnswer()}");""";

        var (beforeKernel, beforeClient) = await CreateKernel("stalepack-before");
        beforeClient.Post(new SubmitCodeRequest(call), o => o.WithTarget(beforeKernel));
        await LogWith(beforeClient, beforeKernel, "stalepack-7").Should().Within(60.Seconds())
            .Emit("the control arm — with an honest build the pack IS on the cell surface");

        // Stage the refusal exactly as ApplyAdoptedSourceStamp records it: the bundle names the
        // sources it was built from and they are not this mesh's.
        await Mesh.GetMeshNodeStream("type/StalePack")
            .Update<NodeTypeDefinition>(d => d with
            {
                AdoptedSourceFingerprint = "bundlefingerprintA",
                CurrentSourceFingerprint = "livefingerprintB",
                BuildProvenance = BuildProvenance.AdoptionRefused,
            })
            .Should().Within(30.Seconds()).Emit();
        await Mesh.GetMeshNodeStream("type/StalePack").Should().Within(30.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition
            {
                BuildProvenance: BuildProvenance.AdoptionRefused
            });

        // A NEW session re-resolves the cell surface. The bytes are unchanged and still perfectly
        // loadable — the ONLY thing that changed is the verdict about where they came from.
        var (afterKernel, afterClient) = await CreateKernel("stalepack-after");
        afterClient.Post(new SubmitCodeRequest(call), o => o.WithTarget(afterKernel));
        await LogWith(afterClient, afterKernel, "CS0103").Should().Within(60.Seconds())
            .Emit("a build PROVEN to come from other source must not be callable from a cell — "
                + "the bare name has to stop resolving, exactly as for a type that never opted in");
    }

    [Fact(Timeout = 120_000)]
    public async Task NonCellSurfacePackType_StaysInvisibleToCells()
    {
        // Determinism's other half: WITHOUT the opt-in, the pack assembly is NOT part of the
        // cell surface — even though its assembly is loaded in this very process (collectible
        // contexts never enter the frozen snapshot; there is no load-order lottery any more).
        var def = await CreateAndCompile(
            "type/HiddenPack",
            new NodeTypeDefinition
            {
                Configuration = "config => config.WithContentType<HiddenPackContent>()"
            },
            ("api", """
                public record HiddenPackContent { public string Title { get; init; } = ""; }
                public static class HiddenPackApi { public static int TheAnswer() => 41; }
                """));
        def.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"the pack type must compile; error: {def.CompilationError}");

        var (kernelAddress, client) = await CreateKernel("hiddenpack");

        client.Post(
            new SubmitCodeRequest("""Console.WriteLine($"hidden-{HiddenPackApi.TheAnswer()}");"""),
            o => o.WithTarget(kernelAddress));
        await LogWith(client, kernelAddress, "CS0103").Should().Within(60.Seconds()).Emit();
    }

    [Fact(Timeout = 120_000)]
    public async Task SharedConsumerOfACellSurfaceType_FailsCompile_NamingTheOwner()
    {
        // Part 3 — single-home enforcement: a `shared=` consumer would recompile the owner's
        // public types into a second assembly (the CS0433 class from Education#171); its
        // compile must fail with a message naming the cell-surface owner.
        var owner = await CreateAndCompile(
            "type/CellOwner",
            new NodeTypeDefinition
            {
                CellSurface = true,
                Configuration = "config => config.WithContentType<CellOwnerContent>()"
            },
            ("api", """
                public record CellOwnerContent { public string Title { get; init; } = ""; }
                public static class CellOwnerApi { public static int One() => 1; }
                """));
        owner.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"the owner must compile; error: {owner.CompilationError}");

        var consumer = await CreateAndCompile(
            "type/CellConsumer",
            new NodeTypeDefinition
            {
                Configuration = "config => config.WithContentType<CellConsumerContent>()",
                Sources = ["namespace:Source scope:subtree", "shared=@type/CellOwner/Source"]
            },
            ("own", """
                public record CellConsumerContent { public string Title { get; init; } = ""; }
                """));

        consumer.CompilationStatus.Should().Be(CompilationStatus.Error,
            "consuming a cell-surface type's Source via shared= is the CS0433 class and must be refused");
        consumer.CompilationError.Should().Contain("type/CellOwner",
            "the failure must NAME the cell-surface owner");
        consumer.CompilationError.Should().Contain("single-home");
    }

    [Fact(Timeout = 120_000)]
    public async Task UnreadableOwner_DegradesLoudly_AndTheCompileProceeds()
    {
        // The gate's fail-open direction, pinned: the consumer's source set reaches into
        // `type/GhostOwner/Source`, but NO NodeType node exists at `type/GhostOwner` — the
        // bounded owner read emits null (absent and stalled are deliberately the same null).
        // That must SKIP that owner's validation with a Warning, never fault the compile:
        // a transient read blip may not park an innocent consumer.
        await MeshService.CreateNode(new MeshNode("ghost", "type/GhostOwner/Source")
        {
            NodeType = "Code",
            Name = "ghost",
            Content = new CodeConfiguration
            {
                Code = """public static class GhostSharedApi { public static int Zero() => 0; }""",
                Language = "csharp"
            },
            State = MeshNodeState.Active
        }).Should().Within(30.Seconds()).Emit();

        var consumer = await CreateAndCompile(
            "type/GhostConsumer",
            new NodeTypeDefinition
            {
                Configuration = "config => config.WithContentType<GhostConsumerContent>()",
                Sources = ["namespace:Source scope:subtree", "shared=@type/GhostOwner/Source"]
            },
            ("own", """
                public record GhostConsumerContent { public string Title { get; init; } = ""; }
                """));

        consumer.CompilationStatus.Should().Be(CompilationStatus.Ok,
            "an unreadable owner skips ITS single-home validation (logged loud) — it must never "
            + $"turn into a compile failure; error: {consumer.CompilationError}");
    }
}
