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
/// 🚨 A replica BELOW a package's dependency floor must not spend the failed-verdict re-drive's
/// automatic attempts. It forms no verdict (#6195), so every flip it made ended at Unavailable
/// ("compile NOT ATTEMPTED"), and on a mixed-version rollout the budget ran out ("GIVING UP") before
/// any replica that meets the floor had its turn — leaving the type to a human with the Compile button.
///
/// <para><b>The run.</b> A type with broken source settles at Error where the floor is met → the
/// floor is lost and the verdict is made stale (a re-drive trigger) → the type must NOT be flipped,
/// refused or Unavailable → the floor is met again and a fresh trigger arrives → it IS re-driven.</para>
/// </summary>
public class ABelowFloorReplicaDoesNotSpendTheFailedVerdictRedriveTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypePath = "type/BelowFloorRedrive";

    private sealed class SwitchableFloor : INodeTypeDependencyFloor
    {
        private int below;

        public bool Below
        {
            get => Volatile.Read(ref below) == 1;
            set => Volatile.Write(ref below, value ? 1 : 0);
        }

        public IObservable<string?> Unmet(string nodeTypePath) =>
            Observable.Defer(() => Observable.Return<string?>(Below
                ? "package 'type' requires AI@^1.40.0, but this replica runs 'MeshWeaver.AI' at 1.39.8"
                : null));
    }

    private readonly SwitchableFloor floor = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<INodeTypeDependencyFloor>(floor));

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private NodeTypeDefinition Definition(MeshNode node) =>
        node.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } definition
            ? definition
            : throw new InvalidOperationException($"{node.Path} does not carry a NodeTypeDefinition");

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
    public async Task ABelowFloorReplica_LeavesAFailedVerdictAlone_UntilTheFloorIsMet()
    {
        // ── 1. A type whose source does not compile settles at Error, floor met. ────────────────
        await MeshService.CreateNode(MeshNode.FromPath(TypePath) with
            {
                Name = "BelowFloorRedrive",
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition
                {
                    Configuration = "config => config.WithContentType<BelowFloorRedriveContent>()",
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
                    Code = "public record BelowFloorRedriveContent { public string Title { get; init; } = NoSuchName; }",
                },
            }))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

        var failed = Definition(await Settled(
            d => d.CompilationStatus == CompilationStatus.Error && d.FailedBuildInputs is not null,
            "the broken source settles at Error with its verdict stamped"));
        var liveStamp = failed.FailedBuildInputs;

        // ── 2. Below the floor, a stale verdict (a re-drive trigger) must not be acted on. ─────
        floor.Below = true;
        const string stale1 = "stale-verdict-one";
        await Write(d => d with { FailedBuildInputs = stale1 }, "the verdict is made stale while below the floor");
        await Task.Delay(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
        var held = Definition(await Mesh.GetMeshNodeStream(TypePath).Take(1).Should().Within(TestTimeouts.CrossSilo)
            .Emit(cancellationToken: TestContext.Current.CancellationToken));
        held.CompilationStatus.Should().Be(CompilationStatus.Error,
            "🚨 a below-floor replica forms no verdict, so it must not flip the type to Pending and leave it "
            + "Unavailable — before the fix every such flip also spent one of the automatic attempts");
        held.FailedBuildInputs.Should().Be(stale1, "nothing re-drove it, so nothing re-stamped the verdict");

        // ── 3. Where the floor is met, a trigger IS re-driven (budget intact). ──────────────────
        floor.Below = false;
        const string stale2 = "stale-verdict-two";
        await Write(d => d with { FailedBuildInputs = stale2 }, "a fresh trigger arrives where the floor is met");
        var redriven = Definition(await Settled(
            d => d.CompilationStatus == CompilationStatus.Error
                 && d.FailedBuildInputs is not null && d.FailedBuildInputs != stale1 && d.FailedBuildInputs != stale2,
            "a replica that meets the floor re-drives the failed verdict and re-stamps it"));
        redriven.FailedBuildInputs.Should().Be(liveStamp, "the re-drive formed its verdict from the live inputs");
    }
}
