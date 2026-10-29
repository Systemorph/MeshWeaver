using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 Systemorph/MeshWeaver#6195 — <b>a type a below-floor replica left <c>Unavailable</c> over its
/// last good build is rebuilt by a replica that meets the floor.</b>
///
/// <para><b>The defect.</b> The floor refusal stamps <see cref="CompilationStatus.Unavailable"/> and
/// keeps the build's coordinates. Every automatic recovery then declined it: the failed-verdict
/// re-drive owns only the NO-build case (<c>HasStaleFailureVerdict</c>), the older-platform trigger
/// reads only <c>Error</c>, and the framework-stale kickoff read only <c>Ok</c>/<c>Error</c>. So once
/// the retained build was stale for the replica that could compile it, the type sat on the
/// Unavailable overlay until a batch bake or a person intervened.</para>
///
/// <para><b>The run.</b> Built Ok with the floor met → this "replica" drops below the floor and a
/// compile is requested (refused: Unavailable, last good build kept) → the floor is met again and the
/// retained build turns out stale here → the owner rebuilds it, Ok, for the live framework. The
/// one seam substituted is the floor, because one test process cannot run two dependency
/// generations.</para>
/// </summary>
public class AFloorRefusedTypeRecoversWhereTheFloorIsMetTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypePath = "type/FloorRecovers";

    private const string UnmetSentence =
        "package 'type' requires AI@^1.40.0, but this replica runs 'MeshWeaver.AI' (package 'AI') "
        + "at 1.39.8, which does not satisfy it";

    /// <summary>The floor as this "replica" judges it — switched by the test.</summary>
    private sealed class SwitchableFloor : INodeTypeDependencyFloor
    {
        private int below;

        public bool Below
        {
            get => Volatile.Read(ref below) == 1;
            set => Volatile.Write(ref below, value ? 1 : 0);
        }

        public IObservable<string?> Unmet(string nodeTypePath) =>
            Observable.Defer(() => Observable.Return<string?>(Below ? UnmetSentence : null));
    }

    private readonly SwitchableFloor floor = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<INodeTypeDependencyFloor>(floor));

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private NodeTypeDefinition Definition(MeshNode node) =>
        node.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;

    private Task<MeshNode> Settled(Func<NodeTypeDefinition, bool> predicate, string because) =>
        Mesh.GetMeshNodeStream(TypePath)
            .Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d && predicate(d),
                because, cancellationToken: TestContext.Current.CancellationToken);

    private Task<MeshNode> Write(Func<NodeTypeDefinition, NodeTypeDefinition> change, string because) =>
        Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.GetMeshNodeStream(TypePath).Update(current =>
                current with { Content = change(Definition(current)) }))
            .Take(1)
            .Should().Within(TestTimeouts.WriteConvergence)
            .Emit(because, cancellationToken: TestContext.Current.CancellationToken);

    [Fact(Timeout = 300_000)]
    public async Task ABuildRefusedBelowTheFloor_IsRebuiltOnceTheFloorIsMet()
    {
        // ── 1. A good build, compiled where the floor is met. ───────────────────────────────────
        await MeshService.CreateNode(MeshNode.FromPath(TypePath) with
            {
                Name = "FloorRecovers",
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition
                {
                    Configuration = "config => config.WithContentType<FloorRecoversContent>()",
                },
            })
            .SelectMany(_ => MeshService.CreateNode(new MeshNode("api", $"{TypePath}/Source")
            {
                NodeType = "Code",
                Name = "api",
                State = MeshNodeState.Active,
                Content = new CodeConfiguration
                {
                    Language = "csharp",
                    Code = "public record FloorRecoversContent { public string Title { get; init; } = \"\"; }",
                },
            }))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

        var good = Definition(await Settled(d => d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error,
            "the first build settles"));
        good.CompilationStatus.Should().Be(CompilationStatus.Ok, $"the source compiles. Error: {good.CompilationError}");
        var goodBuild = good.LatestAssemblyPath;
        goodBuild.Should().NotBeNullOrEmpty();

        // ── 2. Below the floor, a compile is requested and refused. ─────────────────────────────
        floor.Below = true;
        await Write(d => NodeTypeCompilationHelpers.DispatchPending(d, Mesh), "a compile is requested");
        var refused = Definition(await Settled(d => d.CompilationStatus == CompilationStatus.Unavailable,
            "the refusal is a non-verdict"));
        refused.LatestAssemblyPath.Should().Be(goodBuild, "the last good build keeps its coordinates");

        // ── 3. The floor is met here, and the retained build is stale for this replica. ──────────
        // (A replica on another generation is stale against the build a previous one compiled;
        // the store holds nothing under the recorded version for this one.)
        floor.Below = false;
        await Write(d => d with
        {
            CompiledFrameworkVersion = "a-framework-of-another-generation",
            LastCompiledVersion = 987_654_321,
        }, "the retained build is stale on this replica");

        // ── 4. THE RECOVERY. ────────────────────────────────────────────────────────────────────
        var recovered = Definition(await Settled(
            d => d.CompilationStatus == CompilationStatus.Ok
                 && string.Equals(d.CompiledFrameworkVersion, NodeTypeCompilationHelpers.FrameworkVersion, StringComparison.Ordinal),
            "🚨 a replica that meets the floor rebuilds a type a below-floor replica left Unavailable over "
            + "a stale build — before the fix nothing did (#6195)"));
        recovered.LatestAssemblyPath.Should().NotBeNullOrEmpty();
    }

    /// <summary>The state predicate the owner's framework-stale kickoff fires on. Pure.</summary>
    [Theory]
    [InlineData(CompilationStatus.Ok, true)]
    [InlineData(CompilationStatus.Error, true)]
    [InlineData(CompilationStatus.Unavailable, true)]
    [InlineData(CompilationStatus.Pending, false)]
    [InlineData(CompilationStatus.Compiling, false)]
    public void TheKickoff_TreatsUnavailableOverAStaleBuild_AsSettled(CompilationStatus status, bool expected)
    {
        var def = new NodeTypeDefinition
        {
            CompilationStatus = status,
            LatestAssemblyCollection = "assemblies",
            LatestAssemblyPath = "type_FloorRecovers/good.dll",
            CompiledFrameworkVersion = "a-framework-of-another-generation",
            CurrentSourceVersions = ImmutableDictionary<string, long>.Empty,
        };
        NodeTypeCompilationHelpers.IsSettledOnAStaleBuild(def, guards: null).Should().Be(expected);

        // Negative control: the same record with a build for THIS framework is not stale.
        NodeTypeCompilationHelpers.IsSettledOnAStaleBuild(
                def with { CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion }, guards: null)
            .Should().BeFalse();
    }
}
