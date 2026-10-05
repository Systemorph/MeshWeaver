using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>Live module update, slice 2: the swap, against a RUNNING monolith mesh</b> (policy
/// <c>module-live-update-default</c>, <c>Doc/Architecture/LiveModuleUpdate</c>).
///
/// <para>The scenario the maintainer named: an instance is RUNNING with module M at version N
/// serving; generation N+1 of M arrives; it goes live IN THE PROCESS — no restart — the old
/// generation's context is retired and really collected, and the hubs that served N re-instantiate
/// on N+1. Around it: a second update that arrives mid-swap (applied after it — the newest wins), an
/// update while a read is in flight, an update whose contributions cannot be re-applied (refused,
/// N keeps serving, the outcome says a restart is needed and why), an update that fails to build
/// (same), and a NodeType written against a member only N+1 has (the 2026-10-05 incident's
/// <c>ThreadPreparation.Group</c> shape) compiling once N+1 serves.</para>
///
/// <para>The module is a real Roslyn emit installed through the real <c>MeshBuilder.InstallModules</c>;
/// the mesh, its hubs, the recycle and the collection are real.</para>
/// </summary>
public sealed class ModuleLiveSwapTest : MonolithMeshTestBase
{
    private const string Module = "MeshWeaver.Test.LiveSwap";
    private const string ProbePath = "LiveSwapProbe";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private readonly string root = Path.Combine(Path.GetTempPath(), "live-swap-" + Guid.NewGuid().ToString("N"));
    // Written when the mesh is configured — which the base runs from ITS constructor, before this
    // class's constructor body.
    private string? v1Path;

    private string v1 => v1Path ??= Write("g1", ModuleSource(version: 1));

    public ModuleLiveSwapTest(ITestOutputHelper output) : base(output)
    {
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).InstallAssemblies(v1);

    private ModuleContexts Contexts => Mesh.ServiceProvider.GetRequiredService<ModuleContexts>();
    private ModuleLiveUpdater Updater => Mesh.ServiceProvider.GetRequiredService<ModuleLiveUpdater>();
    private CollectibleContextUnloads Unloads => Mesh.ServiceProvider.GetRequiredService<CollectibleContextUnloads>();

    // ─────────────────────────────────────────────── running N → N+1 live, no restart

    [Fact(Timeout = 180_000)]
    public async Task AModuleUpdate_GoesLiveInTheRunningProcess_AndTheOldGenerationIsCollected()
    {
        (await NameAt(ProbePath, TestContext.Current.CancellationToken)).Should().Be("v1", "the arrangement: the instance runs M@N and serves it");
        var weakN = WeakContextOf(Module);

        var outcome = await Swap(Write("g2", ModuleSource(version: 2)), TestContext.Current.CancellationToken);

        outcome.Kind.Should().Be(ModuleSwapKind.Live, outcome.Reason);
        outcome.NeedsRestart.Should().BeFalse("a live module's update must schedule NO restart");
        outcome.Recycled.Should().BeGreaterThan(0, "the hub that served N must be recycled to re-bind");
        (await NameAt(ProbePath, TestContext.Current.CancellationToken)).Should().Be("v2",
            "M@N+1 must serve in the SAME process, with no restart — the whole point of the change");

        var drained = await CollectibleUnloadDrain.WaitUntilCollectedAsync(Unloads);
        drained.Collected.Should().BeTrue($"the retired generation N must really be collected ({drained})");
        weakN.IsAlive.Should().BeFalse();
    }

    /// <summary>
    /// The NEGATIVE CONTROL for the collection above: keep one reference into generation N and the
    /// drain must report N's context RETAINED, by name — so "collected" is a measurement, not a
    /// check that passes on no evidence.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AnOldGenerationSomethingStillHolds_IsReportedRetained_ByItsContextName()
    {
        (await NameAt(ProbePath, TestContext.Current.CancellationToken)).Should().Be("v1");
        var held = HoldGeneration(Module);

        (await Swap(Write("g2", ModuleSource(version: 2)), TestContext.Current.CancellationToken)).Kind.Should().Be(ModuleSwapKind.Live);

        var drained = await CollectibleUnloadDrain.WaitUntilCollectedAsync(Unloads);
        drained.Collected.Should().BeFalse("a reference into N is still held");
        drained.RetainedContexts.Should().Contain(name => name.StartsWith($"{ModuleLoadContext.NamePrefix}{Module}#"),
            "the retention must name the module generation that is held");
        GC.KeepAlive(held);
    }

    // ─────────────────────────────────────────────── two updates, and an update mid-request

    [Fact(Timeout = 180_000)]
    public async Task ASecondUpdateArrivingMidSwap_IsAppliedAfterIt_AndTheNewestServes()
    {
        (await NameAt(ProbePath, TestContext.Current.CancellationToken)).Should().Be("v1");
        var v2 = Write("g2", ModuleSource(version: 2));
        var v3 = Write("g3", ModuleSource(version: 3));

        // Both asked for before either completes — the second arrives while the first is swapping.
        var first = Updater.Swap(v2, "test: first update").Replay(1);
        var second = Updater.Swap(v3, "test: second update").Replay(1);
        using var connectFirst = first.Connect();
        using var connectSecond = second.Connect();

        (await first.Timeout(Budget).Await(TestContext.Current.CancellationToken)).Kind.Should().Be(ModuleSwapKind.Live);
        (await second.Timeout(Budget).Await(TestContext.Current.CancellationToken)).Kind.Should().Be(ModuleSwapKind.Live,
            "the second update is applied AFTER the first, never interleaved with it");
        Contexts.Current(Module)!.Location.Should().Be(Path.GetFullPath(v3), "the newest update wins");
        (await NameAt(ProbePath, TestContext.Current.CancellationToken)).Should().Be("v3");
    }

    [Fact(Timeout = 180_000)]
    public async Task AnUpdateWhileAReadIsInFlight_LeavesTheReadAnswered_AndThenServesN1()
    {
        (await NameAt(ProbePath, TestContext.Current.CancellationToken)).Should().Be("v1");

        var inFlight = ReadNode(ProbePath).Replay(1);
        using var connected = inFlight.Connect();
        var outcome = await Swap(Write("g2", ModuleSource(version: 2)), TestContext.Current.CancellationToken);

        var answered = await inFlight.Timeout(Budget).Await(TestContext.Current.CancellationToken);
        answered.Should().NotBeNull("a read in flight across the swap must be answered, by N or N+1");
        answered!.Name.Should().BeOneOf("v1", "v2");
        outcome.Kind.Should().Be(ModuleSwapKind.Live, outcome.Reason);
        (await NameAt(ProbePath, TestContext.Current.CancellationToken)).Should().Be("v2");
    }

    // ─────────────────────────────────────────────── the refusals: N keeps serving, a restart is named

    [Fact(Timeout = 180_000)]
    public async Task AnUpdateWhoseContributionsCannotBeReapplied_IsRefused_AndNKeepsServing()
    {
        (await NameAt(ProbePath, TestContext.Current.CancellationToken)).Should().Be("v1");

        var outcome = await Swap(Write("g2", ModuleSource(version: 2, configuresMeshHub: true)), TestContext.Current.CancellationToken);

        outcome.Kind.Should().Be(ModuleSwapKind.RestartRequired);
        outcome.NeedsRestart.Should().BeTrue("only a restart can activate a mesh-hub configuration");
        outcome.Reason.Should().Contain(Module).And.Contain("configures the mesh hub");
        Contexts.Current(Module)!.Location.Should().Be(Path.GetFullPath(v1), "never a half-swapped state");
        (await NameAt(ProbePath, TestContext.Current.CancellationToken)).Should().Be("v1", "N keeps serving until the restart");
    }

    [Fact(Timeout = 180_000)]
    public async Task AnUpdateThatFailsToBuild_IsAFailedSwap_AndNKeepsServing()
    {
        (await NameAt(ProbePath, TestContext.Current.CancellationToken)).Should().Be("v1");

        var outcome = await Swap(Write("g2", ModuleSource(version: 2, throwingNodes: true)), TestContext.Current.CancellationToken);

        outcome.Kind.Should().Be(ModuleSwapKind.Failed);
        outcome.NeedsRestart.Should().BeTrue();
        outcome.Reason.Should().Contain("contributions of this generation cannot be built",
            "the recorded reason must name the cause");
        Contexts.Current(Module)!.Location.Should().Be(Path.GetFullPath(v1));
        (await NameAt(ProbePath, TestContext.Current.CancellationToken)).Should().Be("v1");
    }

    [Fact(Timeout = 180_000)]
    public async Task AModuleThatDeclaresRestartRequired_IsNeverSwappedLive_AndTheDeclaredReasonIsTheAnswer()
    {
        (await NameAt(ProbePath, TestContext.Current.CancellationToken)).Should().Be("v1");

        var outcome = await Swap(Write("g2", "[assembly: MeshWeaver.Mesh.ModuleRestartRequired(\"holds a process-wide handle\")]\n"
            + ModuleSource(version: 2)), TestContext.Current.CancellationToken);

        outcome.Kind.Should().Be(ModuleSwapKind.RestartRequired);
        outcome.Reason.Should().Contain("holds a process-wide handle", "the declaration is the explanation the restart carries");
        Contexts.Current(Module)!.Location.Should().Be(Path.GetFullPath(v1));
        (await NameAt(ProbePath, TestContext.Current.CancellationToken)).Should().Be("v1");
    }

    // ─────────────────────────────────────────────── the incident: a NodeType against N+1's member

    /// <summary>
    /// 2026-10-05: AI 1.21.0 landed while the process ran 1.20.4, and every NodeType written against
    /// the new <c>ThreadPreparation.Group</c> failed with CS0117 until a person filed a restart. Here:
    /// a NodeType referencing a member only N+1 has fails to compile while N serves (the negative
    /// half — this is what proves the test can fail), and compiles once N+1 has been swapped in —
    /// in the same process, because the compile reference set follows the module's current
    /// generation.
    /// </summary>
    /// <summary>
    /// 🚨 Disposal keeps the one-outcome contract (#6123 review): a swap queued behind another — or
    /// asked for after the updater is gone — is ANSWERED with a refusal that names the shutdown,
    /// never left without an emission. Before the fix, Dispose tore the Concat down first and the
    /// abandoned job's AsyncSubject never completed, so this test timed out on the queued swap.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ADisposedUpdater_AnswersEveryQueuedAndLateSwap_WithAShutdownRefusal()
    {
        var ct = TestContext.Current.CancellationToken;
        var updater = ActivatorUtilities.CreateInstance<ModuleLiveUpdater>(Mesh.ServiceProvider);
        var first = updater.Swap(Write("g2", ModuleSource(version: 2)), "test: in flight at disposal").Replay(1);
        var queued = updater.Swap(Write("g3", ModuleSource(version: 3)), "test: queued at disposal").Replay(1);
        using var connectFirst = first.Connect();
        using var connectQueued = queued.Connect();

        updater.Dispose();

        (await first.Timeout(Budget).Await(ct)).Should().NotBeNull("the in-flight swap is answered, whatever it reached");
        var abandoned = await queued.Timeout(Budget).Await(ct);
        abandoned.Kind.Should().Be(ModuleSwapKind.Failed, abandoned.Reason);
        abandoned.Reason.Should().Contain("shutting down");
        var late = await updater.Swap(Write("g4", ModuleSource(version: 4)), "test: after disposal").Timeout(Budget).Await(ct);
        late.Kind.Should().Be(ModuleSwapKind.Failed, late.Reason);
        late.NeedsRestart.Should().BeTrue("a swap that was not applied leaves the restart to activate the generation");
    }

    [Fact(Timeout = 300_000)]
    public async Task ANodeTypeWrittenAgainstN1sNewMember_CompilesOnceN1IsSwappedIn()
    {
        const string typePath = "type/GroupedConsumer";
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await mesh.CreateNode(MeshNode.FromPath(typePath) with
            {
                Name = "GroupedConsumer",
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition { Configuration = "config => config.WithContentType<GroupedConsumerContent>()" },
            })
            .SelectMany(_ => mesh.CreateNode(new MeshNode("api", $"{typePath}/Source")
            {
                NodeType = "Code",
                Name = "api",
                State = MeshNodeState.Active,
                Content = new CodeConfiguration
                {
                    Language = "csharp",
                    Code = """
                        public record GroupedConsumerContent { public string Group { get; init; } = MeshWeaver.Test.LiveSwap.Api.Group; }
                        """,
                },
            }))
            .Should().Within(60.Seconds()).Emit(cancellationToken: TestContext.Current.CancellationToken);

        var onN = await Settled(typePath);
        onN.CompilationStatus.Should().Be(CompilationStatus.Error,
            "N lacks Api.Group — the incident's CS0117, and the proof this test can fail");
        onN.CompilationError.Should().Contain("Group");

        (await Swap(Write("g2", ModuleSource(version: 2, withGroup: true)), TestContext.Current.CancellationToken)).Kind.Should().Be(ModuleSwapKind.Live);

        await Mesh.GetMeshNodeStream(typePath)
            .Update(node => node with
            {
                Content = node.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)! with
                {
                    RequestedReleaseAt = DateTimeOffset.UtcNow,
                    RequestedReleaseForce = true,
                },
            })
            .Should().Within(60.Seconds()).Emit(cancellationToken: TestContext.Current.CancellationToken);

        var onN1 = await Mesh.GetMeshNodeStream(typePath)
            .Should().Within(120.Seconds())
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)
                is { CompilationStatus: CompilationStatus.Ok }, cancellationToken: TestContext.Current.CancellationToken);
        onN1.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!.CompilationError.Should().BeNull(
            "the compile after the swap must see N+1's metadata — in the running process");
    }

    // ─────────────────────────────────────────────── helpers

    private async Task<ModuleSwapOutcome> Swap(string location, CancellationToken ct) =>
        await Updater.Swap(location, "test: an update to the module arrived").Timeout(Budget).Await(ct);

    private async Task<string?> NameAt(string path, CancellationToken ct) =>
        (await ReadNode(path).Timeout(Budget).Await(ct))?.Name;

    private async Task<NodeTypeDefinition> Settled(string typePath)
    {
        var settled = await Mesh.GetMeshNodeStream(typePath)
            .Should().Within(120.Seconds())
            .Match(n => n.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)
                is { CompilationStatus: CompilationStatus.Ok or CompilationStatus.Error }, cancellationToken: TestContext.Current.CancellationToken);
        return settled.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference WeakContextOf(string module) => new(Contexts.Current(module)!.Context);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private object HoldGeneration(string module) =>
        Activator.CreateInstance(Contexts.Current(module)!.Assembly.GetType("MeshWeaver.Test.LiveSwap.Api+Token")!)!;

    private static string ModuleSource(int version, bool withGroup = false, bool throwingNodes = false, bool configuresMeshHub = false) => $$"""
        [assembly: MeshWeaver.Test.LiveSwap.Module]
        namespace MeshWeaver.Test.LiveSwap;
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                {{(throwingNodes
                    ? "throw new System.InvalidOperationException(\"contributions of this generation cannot be built\")"
                    : "[new MeshWeaver.Mesh.MeshNode(\"" + ProbePath + "\") { Name = \"v" + version + "\", NodeType = \"Markdown\" }]")}};
            {{(configuresMeshHub
                ? "public override System.Collections.Generic.IEnumerable<System.Func<MeshWeaver.Messaging.MessageHubConfiguration, MeshWeaver.Messaging.MessageHubConfiguration>> HubConfigurations => [c => c];"
                : "")}}
        }
        public static class Api
        {
            public static int Version => {{version}};
            {{(withGroup ? "public static string Group => \"grouped\";" : "")}}
            public sealed class Token { }
        }
        """;

    private string Write(string generation, string source)
    {
        var compilation = CSharpCompilation.Create(
            Module,
            [CSharpSyntaxTree.ParseText(source)],
            PlatformReferences.Platform(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var directory = Path.Combine(root, "modules", $"{Module}@{generation}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Module + ".dll");
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }
}
