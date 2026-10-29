using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 The BATCH bake (<see cref="NodeTypeBatchBake.BakeOne"/>) reports a compile this replica declined
/// below its package's dependency floor as "not evaluated" (<see cref="PreWarmStatus.TimedOut"/>),
/// never <see cref="PreWarmStatus.CompileError"/> — Systemorph/MeshWeaver#6195.
///
/// <para>The stamp already read the shared non-verdict predicate and wrote
/// <see cref="CompilationStatus.Unavailable"/>, but the batch's REPORTED outcome special-cased only
/// <see cref="SourceDiscoveryUnavailableException"/>. So an unmet floor with intact sources reported
/// CompileError: the bake gate cascaded UpstreamFailed to the type's dependents and could refuse
/// readiness for a previously healthy type without Roslyn ever running. The outcome now reads the same
/// predicate the stamp reads. NEGATIVE CONTROL: the identical type with its floor met still compiles.</para>
/// </summary>
public class ABatchBakeBelowADependencyFloorFormsNoVerdictTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan PerTypeBudget = TimeSpan.FromMinutes(3);

    private readonly string suffix = Guid.NewGuid().ToString("N")[..8];
    private string HeldPath => $"{TestPartition}/Held{suffix}";
    private string FreePath => $"{TestPartition}/Free{suffix}";

    private const string Unmet =
        "package 'Hosting' requires AI@^1.40.0, but this replica runs 'MeshWeaver.AI' (package 'AI') "
        + "at 1.39.8, which does not satisfy it";

    /// <summary>Answers <see cref="Unmet"/> for the held type only; met for everything else.</summary>
    private sealed class FixedFloor(Func<string> heldPath) : INodeTypeDependencyFloor
    {
        public IObservable<string?> Unmet(string nodeTypePath) =>
            Observable.Return<string?>(string.Equals(nodeTypePath, heldPath(), StringComparison.Ordinal)
                ? ABatchBakeBelowADependencyFloorFormsNoVerdictTest.Unmet
                : null);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<INodeTypeDependencyFloor>(new FixedFloor(() => HeldPath)));

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private async Task<PreWarmOutcome> Bake(string typePath, CancellationToken ct)
    {
        var id = MeshNode.FromPath(typePath).Id;
        foreach (var node in new[]
                 {
                     new MeshNode(id, TestPartition)
                     {
                         NodeType = MeshNode.NodeTypePath,
                         Name = id,
                         State = MeshNodeState.Active,
                         Content = new NodeTypeDefinition { Sources = ["namespace:Source scope:subtree"] },
                     },
                     new MeshNode("Main", $"{typePath}/Source")
                     {
                         NodeType = "Code",
                         State = MeshNodeState.Active,
                         Content = new CodeConfiguration
                         {
                             Language = "csharp",
                             Code = $"public record {id}Probe(int Value);",
                         },
                     },
                 })
            await MeshService.CreateNode(node).Take(1)
                .Should().Within(TestTimeouts.Convergence).Emit($"{node.Path} must exist before the sweep reads it", ct);

        var enumerated = await Observable.Interval(200.Milliseconds()).StartWith(0L)
            .SelectMany(_ => MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{typePath}").AsSystem()).Take(1))
            .Select(change => DynamicTypePreWarmer.DynamicTypesOf(change.Items, Mesh.JsonSerializerOptions, null))
            .Where(types => types.Definitions.ContainsKey(typePath))
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence).Emit("the sweep enumerates the type", ct);
        var sets = await NodeTypeBatchBake
            .ResolveSources(MeshService, Mesh.ServiceProvider.GetRequiredService<AccessService>(),
                enumerated.Definitions, [typePath], null)
            .Should().Within(TestTimeouts.Convergence).Emit("the batched discovery pass establishes the set", ct);
        sets.TryGetValue(typePath, out var set).Should().BeTrue("the sources are intact — only the floor is unmet");
        set!.Should().NotBeEmpty();
        return await NodeTypeBatchBake
            .BakeOne(Mesh, enumerated.Nodes[typePath], set!, PerTypeBudget, null)
            .Should().Within(PerTypeBudget + TimeSpan.FromMinutes(1)).Emit("BakeOne reaches one outcome", ct);
    }

    [Fact(Timeout = 300_000)]
    public async Task AnUnmetFloor_IsReportedNotEvaluated_AndStampedUnavailable()
    {
        var outcome = await Bake(HeldPath, TestContext.Current.CancellationToken);

        outcome.Status.Should().Be(PreWarmStatus.TimedOut,
            "a compile this replica declined is \"not evaluated\" — CompileError would cascade UpstreamFailed "
            + $"and gate readiness over a verdict nobody formed: {outcome.Detail}");
        outcome.Detail.Should().Contain("AI@^1.40.0");

        await Mesh.GetMeshNodeStream(HeldPath).Should().Within(TestTimeouts.Convergence)
            .Match(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)?.CompilationStatus
                    == CompilationStatus.Unavailable,
                "the stamp and the reported outcome read the same predicate",
                cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 300_000)]
    public async Task NegativeControl_TheSameTypeWithItsFloorMet_Compiles()
    {
        var outcome = await Bake(FreePath, TestContext.Current.CancellationToken);
        outcome.Status.Should().Be(PreWarmStatus.Compiled, outcome.Detail ?? "(no detail)");
    }
}
