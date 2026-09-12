using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
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
/// 🚨 <b>The batch bake compiles the definition the mesh holds WHEN IT COMPILES, never the one it
/// enumerated minutes earlier.</b> Measured on <c>memex.systemorph.com</c>, 2026-09-11, new pod on
/// <c>3.0.0-ci.8372</c> (started 18:50Z) while the Hosting module moved 1.15 → 1.16:
///
/// <code>
/// v458 18:53:04Z  Hosting/Issue  sources: (none — the defaults)        module 1.15
/// v462 18:53:51Z  Hosting/Issue  sources: [namespace:Source scope:subtree,
///                                         shared=@Hosting/InstanceAction/Source/ObservationQueries]  module 1.16
/// ~18:55Z  BatchBake: Executed source queries (2): namespace:Hosting/Issue/Source … / …/Test …
///          Matched: … Hosting/Issue/Source/FleetWatch …    (FleetWatch is the 1.16 file that calls ObservationQueries)
///          CS0103: The name 'ObservationQueries' does not exist in the current context
///          → CompileError on a previously healthy type → "1 NodeType(s) regressed" → readiness refused ~30 min
/// </code>
///
/// <para>The sweep enumerated every NodeType definition ONCE, at its start, and handed that
/// enumeration-time node to <see cref="NodeTypeBatchBake.BakeOne"/> together with a source set the
/// batched discovery pass resolved LATER. So Roslyn was handed neither the 1.15 content nor the 1.16
/// content, but a tear of the two: the 1.15 definition (no <c>shared=</c> entry) over the 1.16
/// source files (a <c>FleetWatch</c> that needs the shared file). Nothing read anything partially and
/// no resolver dropped anything — the definition at v458 genuinely declared no sources; it had simply
/// stopped being the definition by the time it was compiled. A freshly started pod on the same image
/// baked the same type cleanly.</para>
///
/// <para>Every case runs the sweep's own code: the enumeration through
/// <see cref="DynamicTypePreWarmer.DynamicTypesOf"/>, discovery through
/// <see cref="NodeTypeBatchBake.ResolveSources(IMeshService, AccessService, IReadOnlyDictionary{string, NodeTypeDefinition}, IReadOnlyCollection{string}, Microsoft.Extensions.Logging.ILogger)"/>,
/// the compile through <see cref="NodeTypeBatchBake.BakeOne"/> and the real Roslyn pipeline. Nothing
/// is mocked. The controls pin both directions: a definition that did not move still compiles from
/// the batch's own set, and a compile error — whether or not the definition moved — still gates.
/// The last three cases pin the review's findings on the fix (#4051).</para>
/// </summary>
public class ABakeCompilesTheDefinitionItResolvedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string OwnSource = "namespace:Source scope:subtree";
    private static readonly TimeSpan PerTypeBudget = TimeSpan.FromMinutes(3);

    private const string SelfContained =
        "public static class ConsumerApi { public static int Answer() => 1; }";
    private const string CallsTheSharedFile =
        "public static class ConsumerApi { public static int Answer() => LibAnswers.Answer(); }";
    private const string CallsWhatNothingDeclares =
        "public static class ConsumerApi { public static int Answer() => LibAnswers.NotDeclaredAnywhere(); }";

    private readonly string suffix = Guid.NewGuid().ToString("N")[..8];
    private string LibPath => $"{TestPartition}/Lib{suffix}";
    private string TypePath => $"{TestPartition}/Consumer{suffix}";
    private string MainPath => $"{TypePath}/Source/Main";
    private string SharedPath => $"{LibPath}/Source/Answers";
    private string SharedEntry => $"shared=@{SharedPath}";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private MeshNode TypeNode(string id, IReadOnlyList<string>? sources) => new(id, TestPartition)
    {
        NodeType = MeshNode.NodeTypePath,
        Name = id,
        State = MeshNodeState.Active,
        Content = new NodeTypeDefinition { Configuration = "config => config", Sources = sources },
    };

    private static MeshNode Code(string id, string ns, string code) => new(id, ns)
    {
        NodeType = "Code",
        State = MeshNodeState.Active,
        Content = new CodeConfiguration { Language = "csharp", Code = code },
    };

    /// <summary>A library whose ONE shared file the consumer may pull in, and the consumer itself.</summary>
    private async Task Seed(string consumerCode, IReadOnlyList<string>? consumerSources)
    {
        foreach (var node in new[]
                 {
                     TypeNode($"Lib{suffix}", null),
                     Code("Answers", $"{LibPath}/Source",
                         "public static class LibAnswers { public static int Answer() => 42; }"),
                     TypeNode($"Consumer{suffix}", consumerSources),
                     Code("Main", $"{TypePath}/Source", consumerCode),
                 })
            await MeshService.CreateNode(node).Take(1)
                .Should().Within(TestTimeouts.Convergence).Emit($"{node.Path} must exist before the sweep reads it");
    }

    /// <summary>
    /// The sweep's enumeration, exactly as <c>WarmDynamicTypes</c> takes it — a listing typed through
    /// <see cref="DynamicTypePreWarmer.DynamicTypesOf"/> — waited on until the listing shows the state
    /// under test (the index trails the store, so "listed" implies "stored").
    /// </summary>
    private async Task<DynamicTypePreWarmer.DynamicTypes> Enumerate(Func<NodeTypeDefinition, bool> state, string because)
        => await Observable.Interval(200.Milliseconds()).StartWith(0L)
            .SelectMany(_ => MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{TypePath}").AsSystem())
                .Take(1))
            .Select(change => DynamicTypePreWarmer.DynamicTypesOf(change.Items, Mesh.JsonSerializerOptions, null))
            .Where(types => types.Definitions.TryGetValue(TypePath, out var d) && d is not null && state(d))
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence).Emit(because);

    private async Task UntilListed(string path, string marker)
        => await Observable.Interval(200.Milliseconds()).StartWith(0L)
            .SelectMany(_ => MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}").AsSystem())
                .Take(1))
            .Where(change => change.Items.Any(n =>
                n.ContentAs<CodeConfiguration>(Mesh.JsonSerializerOptions)?.Code?.Contains(marker) == true))
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence).Emit($"the discovery pass must be able to see '{marker}' in {path}");

    /// <summary>The module update, landing in the order a repository sync lands it: the file, then the definition.</summary>
    private async Task LandTheUpdate(string newCode, IReadOnlyList<string> newSources)
    {
        var workspace = Mesh.GetWorkspace();
        await workspace.GetMeshNodeStream(MainPath)
            .Update(node => node with { Content = new CodeConfiguration { Language = "csharp", Code = newCode } })
            .Should().Within(TestTimeouts.Convergence).Emit("the source half of the update lands");
        await workspace.GetMeshNodeStream(TypePath)
            .Update(node => node with
            {
                Content = node.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)! with { Sources = newSources },
            })
            .Should().Within(TestTimeouts.Convergence).Emit("the definition half of the update lands");
    }

    /// <summary>The sweep's batch pass for this one type: discovery from the ENUMERATED definitions, then the compile.</summary>
    private async Task<(IReadOnlyList<MeshNode> Batch, PreWarmOutcome Outcome)> Bake(
        DynamicTypePreWarmer.DynamicTypes enumerated, TimeSpan? budget = null)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var sets = await NodeTypeBatchBake
            .ResolveSources(MeshService, access, enumerated.Definitions, [TypePath], null)
            .Should().Within(TestTimeouts.Convergence).Emit("the batched discovery pass must establish the source sets");
        var batch = sets.TryGetValue(TypePath, out var set) ? set : [];
        var outcome = await NodeTypeBatchBake
            .BakeOne(Mesh, enumerated.Nodes[TypePath], batch, budget ?? PerTypeBudget, null)
            .Should().Within(PerTypeBudget + TimeSpan.FromMinutes(1)).Emit("BakeOne always reaches exactly one outcome");
        Output.WriteLine("batch set: {0}", string.Join(", ", batch.Select(n => n.Path)));
        Output.WriteLine("outcome: {0} — {1}", outcome.Status, outcome.Detail ?? "(no detail)");
        return (batch, outcome);
    }

    /// <summary>
    /// 🚨 THE REPRO. The definition moves between the enumeration and the compile — as Hosting/Issue's
    /// did — and the verdict must be the one the CURRENT content earns: a clean compile when it is
    /// sound, and still a gating <see cref="PreWarmStatus.CompileError"/> when it is not.
    /// </summary>
    [Theory(Timeout = 300_000)]
    [InlineData(CallsTheSharedFile, PreWarmStatus.Compiled)]
    [InlineData(CallsWhatNothingDeclares, PreWarmStatus.CompileError)]
    public async Task ADefinitionThatMovedDuringTheSweep_IsJudgedAsItNowStands(string newCode, PreWarmStatus expected)
    {
        await Seed(SelfContained, consumerSources: null);
        var enumerated = await Enumerate(d => d.Sources is null,
            "the sweep enumerates the type BEFORE the module update, with no declared sources");

        await LandTheUpdate(newCode, [OwnSource, SharedEntry]);
        await UntilListed(MainPath, newCode == CallsTheSharedFile ? "LibAnswers.Answer()" : "NotDeclaredAnywhere");
        await Enumerate(d => d.Sources?.Contains(SharedEntry) == true,
            "the updated definition is stored before the compile runs — as v471 was, ~1 min before the pod compiled");

        var (batch, outcome) = await Bake(enumerated);

        batch.Select(n => n.Path).Should().Contain(MainPath).And.NotContain(SharedPath,
            "precondition — the pass selected the set with the ENUMERATED queries, which never named the shared "
            + "file; this is the half of the tear the compile has to see past");
        outcome.Status.Should().Be(expected,
            $"the compile must judge the definition the mesh holds now, not the enumeration's ({outcome.Detail})");
        if (expected is PreWarmStatus.CompileError)
            outcome.Detail.Should().Contain("NotDeclaredAnywhere",
                "the gating verdict names the genuine defect, not a phantom about the shared file");
    }

    /// <summary>🚨 CONTROL — a definition that did NOT move compiles from the batch's own set, as it always has.</summary>
    [Fact(Timeout = 300_000)]
    public async Task ADefinitionThatDidNotMove_CompilesFromTheBatchSet()
    {
        await Seed(CallsTheSharedFile, [OwnSource, SharedEntry]);
        await UntilListed(MainPath, "LibAnswers.Answer()");
        var enumerated = await Enumerate(d => d.Sources?.Contains(SharedEntry) == true,
            "the sweep enumerates the already-updated definition");

        var (batch, outcome) = await Bake(enumerated);

        batch.Select(n => n.Path).Should().Contain(new[] { MainPath, SharedPath },
            "the batch resolves the single-node shared= entry through its path: leg");
        outcome.Status.Should().Be(PreWarmStatus.Compiled, outcome.Detail ?? "(no detail)");
    }

    /// <summary>🚨 CONTROL — a genuine compile error on a definition that did not move still gates.</summary>
    [Fact(Timeout = 300_000)]
    public async Task AGenuineCompileError_StillGates()
    {
        await Seed(CallsWhatNothingDeclares, [OwnSource, SharedEntry]);
        await UntilListed(MainPath, "NotDeclaredAnywhere");
        var enumerated = await Enumerate(d => d.Sources?.Contains(SharedEntry) == true,
            "the sweep enumerates the definition it compiles");

        var (_, outcome) = await Bake(enumerated);

        outcome.Status.Should().Be(PreWarmStatus.CompileError, outcome.Detail ?? "(no detail)");
        outcome.Detail.Should().Contain("NotDeclaredAnywhere");
    }

    // ── The review's findings (#4051) ──────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 The same queries are NOT the same set. A source edited after the batch resolved it — the
    /// type's queries untouched — is compiled as it now stands once the type's own source-version
    /// record has moved: the batch holds the broken edit it saw, the mesh holds the repair.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ASourceEditedUnderUnchangedQueries_IsCompiledAsItNowStands()
    {
        await Seed(CallsWhatNothingDeclares, consumerSources: null);
        await UntilListed(MainPath, "NotDeclaredAnywhere");
        var enumerated = await Enumerate(d => d.Sources is null, "the sweep enumerates the type");
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var sets = await NodeTypeBatchBake
            .ResolveSources(MeshService, access, enumerated.Definitions, [TypePath], null)
            .Should().Within(TestTimeouts.Convergence).Emit("the batch resolves the broken edit");
        var batch = sets[TypePath];

        // The repair lands under the SAME queries, after the batch took its set.
        await Mesh.GetWorkspace().GetMeshNodeStream(MainPath)
            .Update(node => node with { Content = new CodeConfiguration { Language = "csharp", Code = SelfContained } })
            .Should().Within(TestTimeouts.Convergence).Emit("the repair lands");
        var repaired = await Observable.Interval(200.Milliseconds()).StartWith(0L)
            .SelectMany(_ => MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{MainPath}").AsSystem()).Take(1))
            .Select(change => change.Items.FirstOrDefault(n =>
                n.ContentAs<CodeConfiguration>(Mesh.JsonSerializerOptions)?.Code == SelfContained))
            .Where(n => n is not null)
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence).Emit("the repair is listed");
        var repairedVersion = NodeTypeDefinition.SourceVersionOf(repaired!);

        // The type's own sources watcher records the repair — its hub activated by reading its stream.
        using var activation = Mesh.GetWorkspace().GetMeshNodeStream(TypePath).Subscribe(
            _ => { }, ex => Output.WriteLine("type stream faulted: {0}", ex.Message));
        await Enumerate(d => d.CurrentSourceVersions is { } record
                && record.TryGetValue(MainPath, out var v) && v == repairedVersion,
            "the type's source-version record names the repair before the compile runs");

        batch.Select(n => n.ContentAs<CodeConfiguration>(Mesh.JsonSerializerOptions)?.Code)
            .Should().Contain(CallsWhatNothingDeclares, "precondition — the batch still holds the broken edit");
        var outcome = await NodeTypeBatchBake
            .BakeOne(Mesh, enumerated.Nodes[TypePath], batch, PerTypeBudget, null)
            .Should().Within(PerTypeBudget + TimeSpan.FromMinutes(1)).Emit("BakeOne always reaches exactly one outcome");
        Output.WriteLine("outcome: {0} — {1}", outcome.Status, outcome.Detail ?? "(no detail)");

        outcome.Status.Should().Be(PreWarmStatus.Compiled,
            $"the record says the source moved, so the batch's copy is not the set ({outcome.Detail})");
    }

    /// <summary>
    /// 🚨 A moved definition whose CURRENT source set cannot be established is "not evaluated" —
    /// never a compile of the stale pair and never a gating verdict. Driven by the per-type deadline:
    /// every discovery query waits out a one-second quiet window, so an 800 ms budget cannot
    /// establish the set, structurally rather than by timing luck.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AMovedDefinitionWhoseSourcesCannotBeEstablished_IsNotEvaluated()
    {
        await Seed(SelfContained, consumerSources: null);
        var enumerated = await Enumerate(d => d.Sources is null, "the sweep enumerates the type first");
        await LandTheUpdate(CallsTheSharedFile, [OwnSource, SharedEntry]);
        await UntilListed(MainPath, "LibAnswers.Answer()");
        await Enumerate(d => d.Sources?.Contains(SharedEntry) == true, "the moved definition is stored");

        var (_, outcome) = await Bake(enumerated, TimeSpan.FromMilliseconds(800));

        outcome.Status.Should().Be(PreWarmStatus.TimedOut,
            $"an unestablished input is not a verdict about the code ({outcome.Detail})");
        outcome.Detail.Should().Contain("not evaluated");
    }

    /// <summary>
    /// 🚨 A type its repository pruned during the sweep is <see cref="PreWarmStatus.Removed"/>, and
    /// the bake does NOT write it back: the stamp's insert-if-absent used to re-create it.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ATypePrunedDuringTheSweep_IsRemoved_AndNotRecreated()
    {
        await MeshService.CreateNode(TypeNode($"Consumer{suffix}", null)).Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit("the type exists when the sweep enumerates it");
        var enumerated = await Enumerate(_ => true, "the sweep enumerates the type");

        await MeshService.DeleteNode(TypePath).Take(1).DefaultIfEmpty()
            .Should().Within(TestTimeouts.Convergence).Emit("the repository prunes the type");
        await Observable.Interval(200.Milliseconds()).StartWith(0L)
            .SelectMany(_ => DynamicTypePreWarmer.TypeNodeExists(Mesh, TypePath, null).Take(1))
            .Where(exists => !exists)
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence).Emit("the prune is visible to a listing");

        var (_, outcome) = await Bake(enumerated);

        outcome.Status.Should().Be(PreWarmStatus.Removed, outcome.Detail ?? "(no detail)");
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var row = await storage.Read(TypePath, Mesh.JsonSerializerOptions).Take(1).DefaultIfEmpty()
            .Should().Within(TestTimeouts.Convergence).Emit("the storage read answers");
        row.Should().BeNull("the bake must not re-create a type its repository pruned");
    }
}
