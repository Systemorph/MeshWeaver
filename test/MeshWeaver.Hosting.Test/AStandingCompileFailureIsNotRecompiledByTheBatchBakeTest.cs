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
/// 🚨 The BATCH bake must not compile a type again whose standing failure was already measured under
/// the inputs live now — MeshWeaver#8529 (328 occurrences of one <c>CompilationException</c> on
/// memex-cloud, still rising).
///
/// <para>The bake classifies every <c>CompilationStatus.Error</c> as <c>PreviouslyBroken</c> ⇒ needs a
/// bake, and re-compiled it on EVERY boot of EVERY pod: six broken user types × ~58 pods in two days
/// of rolls, ~4 minutes and ~2.9 GB of working set per pod, the identical <c>CS0246</c> each time. The
/// activation path stamps what a verdict was formed from and re-drives once per distinct input set;
/// the bake never read the stamp. These tests drive <see cref="NodeTypeBatchBake.BakeOne"/> against a
/// real mesh: the first attempt fails and stamps; the second is DECLINED with no compile; an edit to
/// the source earns a real attempt again (NEGATIVE CONTROL — the decline is memoisation, not a cap).</para>
/// </summary>
public class AStandingCompileFailureIsNotRecompiledByTheBatchBakeTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan PerTypeBudget = TimeSpan.FromMinutes(3);
    private const string DeclinedMarker = "not re-attempted";

    private readonly string suffix = Guid.NewGuid().ToString("N")[..8];
    private string TypePath => $"{TestPartition}/Broken{suffix}";
    private string SourcePath => $"{TypePath}/Source/Main";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static CodeConfiguration Code(string id, string body) => new()
    {
        Language = "csharp",
        Code = $"public record {id}Probe({body});",
    };

    private async Task Seed(CancellationToken ct)
    {
        var id = MeshNode.FromPath(TypePath).Id;
        foreach (var node in new[]
                 {
                     new MeshNode(id, TestPartition)
                     {
                         NodeType = MeshNode.NodeTypePath,
                         Name = id,
                         State = MeshNodeState.Active,
                         Content = new NodeTypeDefinition { Sources = ["namespace:Source scope:subtree"] },
                     },
                     new MeshNode("Main", $"{TypePath}/Source")
                     {
                         NodeType = "Code",
                         State = MeshNodeState.Active,
                         // CS0246: a type nobody defines.
                         Content = Code(id, "NobodyDefinesThis Value"),
                     },
                 })
            await MeshService.CreateNode(node).Take(1)
                .Should().Within(TestTimeouts.Convergence).Emit($"{node.Path} must exist before the sweep reads it", ct);
    }

    /// <summary>One sweep's worth of one type: re-enumerate (so the stored stamp is read), resolve the
    /// sources fresh, bake.</summary>
    private async Task<PreWarmOutcome> Bake(CancellationToken ct)
    {
        var enumerated = await Observable.Interval(200.Milliseconds()).StartWith(0L)
            .SelectMany(_ => MeshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{TypePath}").AsSystem()).Take(1))
            .Select(change => DynamicTypePreWarmer.DynamicTypesOf(change.Items, Mesh.JsonSerializerOptions, null))
            .Where(types => types.Definitions.ContainsKey(TypePath))
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence).Emit("the sweep enumerates the type", ct);
        var sets = await NodeTypeBatchBake
            .ResolveSources(MeshService, Mesh.ServiceProvider.GetRequiredService<AccessService>(),
                enumerated.Definitions, [TypePath], null)
            .Should().Within(TestTimeouts.Convergence).Emit("the batched discovery pass establishes the set", ct);
        sets.TryGetValue(TypePath, out var set).Should().BeTrue();
        return await NodeTypeBatchBake
            .BakeOne(Mesh, enumerated.Nodes[TypePath], set!, PerTypeBudget, null)
            .Should().Within(PerTypeBudget + TimeSpan.FromMinutes(1)).Emit("BakeOne reaches one outcome", ct);
    }

    private async Task AwaitStampedError(CancellationToken ct) =>
        await Mesh.GetMeshNodeStream(TypePath).Should().Within(TestTimeouts.Convergence)
            .Match(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is
                    { CompilationStatus: CompilationStatus.Error, FailedBuildInputs.Length: > 0 },
                "the failed first attempt stamps what the verdict was formed from",
                cancellationToken: ct);

    [Fact(Timeout = 300_000)]
    public async Task ASecondBake_UnderTheSameInputs_DeclinesWithoutCompiling()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);

        var first = await Bake(ct);
        first.Status.Should().Be(PreWarmStatus.CompileError);
        first.Detail.Should().NotContain(DeclinedMarker, "the FIRST attempt always runs");
        await AwaitStampedError(ct);

        var second = await Bake(ct);
        second.Status.Should().Be(PreWarmStatus.CompileError, "the verdict is unchanged");
        second.Detail.Should().Contain(DeclinedMarker,
            "the same framework, modules and sources fail identically — a compile would only repeat it");

        var third = await Bake(ct);
        third.Detail.Should().Contain(DeclinedMarker, "and it stays declined on every later boot");
    }

    [Fact(Timeout = 300_000)]
    public async Task NegativeControl_AnEditedSource_EarnsAFreshAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        (await Bake(ct)).Status.Should().Be(PreWarmStatus.CompileError);
        await AwaitStampedError(ct);
        (await Bake(ct)).Detail.Should().Contain(DeclinedMarker);

        // The author fixes the code. LastModified moves, so the live source token no longer equals the
        // one the verdict was formed from.
        var id = MeshNode.FromPath(TypePath).Id;
        await Mesh.GetMeshNodeStream(SourcePath)
            .Update(n => n with { Content = Code(id, "int Value") })
            .Should().Within(TestTimeouts.Convergence).Emit("the fix lands", ct);

        var fixedUp = await Bake(ct);
        fixedUp.Status.Should().Be(PreWarmStatus.Compiled,
            "a changed source is a new input — the decline is memoisation of a deterministic function, "
            + $"never a latch: {fixedUp.Detail}");
    }
}
