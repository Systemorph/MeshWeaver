using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A read of a dynamic type that is BUILT here must come back TYPED — even when nothing has
/// registered the type on this replica yet</b> (Systemorph/MeshWeaver.Plugins#2799,
/// <c>Doc/Architecture/DynamicContentTypeRegistration</c> → "A read never waits for the pass").
///
/// <para>Measured on memex-cloud (pod <c>884964bb7-6gv59</c>, 2026-10-09): 132 of 134 untyped reads
/// happened at 19:48:37.3, before the pre-warmer started, from the process's OWN boot-time readers —
/// of types whose bytes were all on the replica (<c>alreadyBaked=414</c>, <c>compiled=0</c>). The
/// registration-only pass ran minutes later; a query result read in that window stayed untyped
/// until something changed it.</para>
///
/// <para>This drives the real pieces, nothing mocked: the batch bake compiles the type WITHOUT
/// activating a hub (bytes and record present, nothing registered — the boot state), the instance
/// is written straight to the store (so no instance hub activates and registers it as a side
/// effect), and the production read seams type the read. <b>SHOULD-FAIL-IF</b> the seams stop
/// asking for the registration (the negative control — the same read with no on-demand
/// registration, which is current main — answers an untyped <see cref="JsonElement"/>), or if the
/// registration heals by COMPILING (the record's build stamp would move).</para>
/// </summary>
public class AReadAtBootIsTypedNotUntypedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan PerTypeBudget = TimeSpan.FromMinutes(3);

    private readonly string suffix = Guid.NewGuid().ToString("N")[..8];
    private string TypeId => $"Widget{suffix}";
    private string TypePath => $"{TestPartition}/{TypeId}";
    private string ContentTypeName => $"WidgetContent{suffix}";
    private string InstancePath => $"{TestPartition}/Item{suffix}";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .ConfigureServices(services => services.AddSingleton<ContentTypeOnDemandRegistration>());

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private IMeshContentTypeRegistry Registry => Mesh.ServiceProvider.GetRequiredService<IMeshContentTypeRegistry>();
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
    private ContentTypeOnDemandRegistration OnDemand => Mesh.ServiceProvider.GetRequiredService<ContentTypeOnDemandRegistration>();

    private MeshNode Instance => new($"Item{suffix}", TestPartition)
    {
        NodeType = TypePath,
        State = MeshNodeState.Active,
        Content = JsonSerializer.Deserialize<JsonElement>(
            $$"""{"$type":"{{ContentTypeName}}","label":"read at boot"}"""),
    };

    private async Task Seed(CancellationToken cancellationToken)
    {
        foreach (var node in new[]
                 {
                     new MeshNode(TypeId, TestPartition)
                     {
                         NodeType = MeshNode.NodeTypePath,
                         Name = TypeId,
                         State = MeshNodeState.Active,
                         Content = new NodeTypeDefinition
                         {
                             Configuration = $"config => config.WithContentType<{ContentTypeName}>()",
                         },
                     },
                     new MeshNode("Main", $"{TypePath}/Source")
                     {
                         NodeType = "Code",
                         State = MeshNodeState.Active,
                         Content = new CodeConfiguration
                         {
                             Language = "csharp",
                             Code = $"public record {ContentTypeName} {{ public string? Label {{ get; init; }} }}",
                         },
                     },
                 })
            await MeshService.CreateNode(node).Take(1)
                .Should().Within(TestTimeouts.Convergence)
                .Emit($"{node.Path} must exist before the bake reads it", cancellationToken);
    }

    private async Task<DynamicTypePreWarmer.DynamicTypes> Enumerate(
        Func<NodeTypeDefinition, bool> state, string because, CancellationToken cancellationToken)
        => await Observable.Interval(200.Milliseconds()).StartWith(0L)
            .SelectMany(_ => MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{TypePath}").AsSystem())
                .Take(1))
            .Select(change => DynamicTypePreWarmer.DynamicTypesOf(change.Items, Mesh.JsonSerializerOptions, null))
            .Where(types => types.Definitions.TryGetValue(TypePath, out var d) && d is not null && state(d))
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence).Emit(because, cancellationToken);

    /// <summary>Bakes the type the way the gated sweep does — a direct compile, no hub activation.</summary>
    private async Task Bake(CancellationToken cancellationToken)
    {
        var enumerated = await Enumerate(_ => true, "the sweep enumerates the type", cancellationToken);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var sets = await NodeTypeBatchBake
            .ResolveSources(MeshService, access, enumerated.Definitions, [TypePath], null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the batched discovery pass must establish the source set", cancellationToken);
        var outcome = await NodeTypeBatchBake
            .BakeOne(Mesh, enumerated.Nodes[TypePath], sets.TryGetValue(TypePath, out var set) ? set : [],
                PerTypeBudget, null)
            .Should().Within(PerTypeBudget + TimeSpan.FromMinutes(1))
            .Emit("BakeOne always reaches exactly one outcome", cancellationToken);
        outcome.Status.Should().Be(PreWarmStatus.Compiled, outcome.Detail ?? "(no detail)");
        await Enumerate(
            d => !string.IsNullOrEmpty(d.LatestAssemblyPath) && d.LastCompiledVersion is not null,
            "the bake's build stamp lands on the NodeType record", cancellationToken);
    }

    /// <summary>The NodeType's definition read straight from the store — the authority.</summary>
    private async Task<NodeTypeDefinition> Stored(CancellationToken cancellationToken)
    {
        var node = await Storage
            .Read(TypePath, Mesh.JsonSerializerOptions)
            .Should().Within(TestTimeouts.Convergence).Emit("the NodeType row is readable", cancellationToken);
        node.Should().NotBeNull();
        return node!.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
    }

    private Func<string, IObservable<ContentTypeRegistrationOutcome>> Ensure =>
        nodeType => OnDemand.EnsureRegistered(Mesh, nodeType);

    /// <summary>
    /// 🚨 THE REPRO AND THE FIX, through the QUERY seam — the one with no late re-type, so an
    /// untyped answer at boot stayed untyped. Control first: the same read without on-demand
    /// registration (current main) answers untyped. Then the production seam answers typed on its
    /// FIRST emission, and the record's build stamp has not moved.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AQueryRead_OfABuiltButUnregisteredType_IsTypedOnItsFirstEmission()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        await Bake(ct);
        var before = await Stored(ct);
        before.LatestAssemblyPath.Should().NotBeNullOrEmpty("the store holds the bake's stamp");
        Registry.TryResolveByNodeType(TypePath, out _).Should().BeFalse(
            "CONTROL — bytes and record present, nothing registered: the boot state. If this ever "
            + "holds, the test no longer measures the gap.");

        var raw = Observable.Return<IEnumerable<MeshNode>>([Instance]);
        var withoutOnDemand = await MeshNodeStreamCache
            .WrapWithOptions(raw, Mesh.JsonSerializerOptions, NullLogger.Instance, Registry)
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence).Emit("the control read answers", ct);
        withoutOnDemand.Single().Content.Should().BeOfType<JsonElement>(
            "NEGATIVE CONTROL — without on-demand registration (current main) the boot read answers "
            + "an untyped element; if this is typed, the test no longer reproduces the defect");

        // End to end: the instance is in the store (written past every hub, so no activation
        // registers the type), and the REAL cache's query answers it.
        await Storage.Write(Instance, Mesh.JsonSerializerOptions).Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit("the instance lands in the store", ct);
        var cache = (MeshNodeStreamCache)Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var first = await cache.GetQuery($"boot-read-{suffix}", Mesh.JsonSerializerOptions, $"path:{InstancePath}")
            .Where(nodes => nodes.Any())
            .FirstAsync()
            .Should().Within(PerTypeBudget).Emit("the query answers the stored instance", ct);

        var content = first.Single().Content;
        content.Should().NotBeOfType<JsonElement>("a read of a BUILT type must be typed on its first emission");
        content!.GetType().Name.Should().Be(ContentTypeName);
        content.GetType().Assembly.IsCollectible.Should().BeTrue(
            "the content is typed from the baked (collectible) assembly, not a stand-in");

        var after = await Stored(ct);
        after.LatestAssemblyPath.Should().Be(before.LatestAssemblyPath,
            "registration on a read must never heal by COMPILING — the unsafe shape");
        after.LastCompiledVersion.Should().Be(before.LastCompiledVersion);
        after.LastCompileSucceededAt.Should().Be(before.LastCompileSucceededAt);
    }

    /// <summary>The same property through the GetStream seam (a standing watch read at boot).</summary>
    [Fact(Timeout = 300_000)]
    public async Task AStreamRead_OfABuiltButUnregisteredType_IsTypedOnItsFirstEmission()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        await Bake(ct);
        Registry.TryResolveByNodeType(TypePath, out _).Should().BeFalse("CONTROL — the boot state");

        var control = await MeshNodeStreamCache
            .TypeStream(Observable.Return(Instance), Mesh.JsonSerializerOptions, NullLogger.Instance, Registry,
                degradations: null, ensureRegistered: null)
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence).Emit("the control read answers", ct);
        control.Content.Should().BeOfType<JsonElement>("NEGATIVE CONTROL — current main answers untyped");

        var typed = await MeshNodeStreamCache
            .TypeStream(Observable.Return(Instance), Mesh.JsonSerializerOptions, NullLogger.Instance, Registry,
                degradations: null, ensureRegistered: Ensure)
            .ToArray()
            .Should().Within(PerTypeBudget).Emit("the stream answers and completes with its source", ct);

        typed.Should().ContainSingle("one raw emission is one typed emission — never an untyped one first");
        typed[0].Content!.GetType().Name.Should().Be(ContentTypeName);
    }

    /// <summary>
    /// 🚨 CONTROL — a type with NO build is never compiled on a read: the registration answers
    /// NotBaked at once, the read degrades as before, and the record stays unbuilt.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AReadOfATypeWithNoBuild_DegradesAsBefore_AndCompilesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await Seed(ct);
        await Enumerate(_ => true, "the type's record is listed", ct);

        var outcome = await OnDemand.EnsureRegistered(Mesh, TypePath)
            .Should().Within(TestTimeouts.Convergence).Emit("the attempt reaches one outcome", ct);
        outcome.Status.Should().Be(ContentTypeRegistrationStatus.NotBaked, outcome.Detail ?? "(no detail)");

        var read = await MeshNodeStreamCache
            .TypeStream(Observable.Return(Instance), Mesh.JsonSerializerOptions, NullLogger.Instance, Registry,
                degradations: null, ensureRegistered: Ensure)
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence).Emit("the read answers", ct);
        read.Content.Should().BeOfType<JsonElement>();
        Registry.TryResolveByNodeType(TypePath, out _).Should().BeFalse();
        (await Stored(ct)).LatestAssemblyPath.Should().BeNullOrEmpty("a read compiled nothing");
    }
}
