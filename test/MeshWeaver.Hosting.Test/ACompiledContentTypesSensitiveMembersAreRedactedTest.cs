using System;
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
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b><see cref="SensitiveDataAttribute"/> reaches a content type compiled LIVE from node
/// source.</b> A NodeType's content record is written in the mesh, compiled by the real batch bake
/// (Roslyn, the assembly store, a collectible load context) and registered with the mesh-wide
/// content-type registry — the state of every in-mesh module type. Its marked member must be
/// redacted both as a typed CLR instance (reflection across the load context) and as untyped JSON
/// whose <c>$type</c> names it (the registry resolves the discriminator).
/// See <c>Doc/Architecture/SensitiveContentInLogs</c>.
/// </summary>
public class ACompiledContentTypesSensitiveMembersAreRedactedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan PerTypeBudget = TimeSpan.FromMinutes(3);
    private const string Diagnosis = "DIAGNOSIS-compiled-0a7d3b";
    private const string Ward = "WARD-compiled-west";

    private readonly string suffix = Guid.NewGuid().ToString("N")[..8];
    private string TypeId => $"Claimant{suffix}";
    private string TypePath => $"{TestPartition}/{TypeId}";
    private string ContentTypeName => $"ClaimantContent{suffix}";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private IMeshContentTypeRegistry Registry => Mesh.ServiceProvider.GetRequiredService<IMeshContentTypeRegistry>();

    private async Task<Type> CompileAndRegister(CancellationToken ct)
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
                             // MeshWeaver.Messaging is a standard using of every in-mesh compile.
                             Code = $"public record {ContentTypeName}([SensitiveData] string? Diagnosis = null) {{ public string? Ward {{ get; init; }} }}",
                         },
                     },
                 })
            await MeshService.CreateNode(node).Take(1)
                .Should().Within(TestTimeouts.Convergence).Emit($"{node.Path} must exist", ct);

        var enumerated = await Enumerate(_ => true, "the sweep enumerates the type", ct);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var sets = await NodeTypeBatchBake
            .ResolveSources(MeshService, access, enumerated.Definitions, [TypePath], null)
            .Should().Within(TestTimeouts.Convergence).Emit("the source set is established", ct);
        var outcome = await NodeTypeBatchBake
            .BakeOne(Mesh, enumerated.Nodes[TypePath], sets.TryGetValue(TypePath, out var set) ? set : [],
                PerTypeBudget, null)
            .Should().Within(PerTypeBudget + TimeSpan.FromMinutes(1)).Emit("BakeOne reaches one outcome", ct);
        outcome.Status.Should().Be(PreWarmStatus.Compiled, outcome.Detail ?? "(no detail)");
        var baked = await Enumerate(d => !string.IsNullOrEmpty(d.LatestAssemblyPath) && d.LastCompiledVersion is not null,
            "the bake's build stamp lands on the NodeType record", ct);
        await DynamicContentTypeRegistrar.RegisterTypes(Mesh, baked, TimeSpan.Zero, null).ToArray()
            .Should().Within(PerTypeBudget).Emit("the registration pass completes", ct);

        Registry.TryResolveByNodeType(TypePath, out var compiled).Should().BeTrue("the compiled type is registered");
        compiled.Assembly.IsCollectible.Should().BeTrue("the type comes from the live compile, not a stand-in");
        return compiled;
    }

    private async Task<DynamicTypePreWarmer.DynamicTypes> Enumerate(
        Func<NodeTypeDefinition, bool> state, string because, CancellationToken ct)
        => await Observable.Interval(200.Milliseconds()).StartWith(0L)
            .SelectMany(_ => MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{TypePath}").AsSystem())
                .Take(1))
            .Select(change => DynamicTypePreWarmer.DynamicTypesOf(change.Items, Mesh.JsonSerializerOptions, null))
            .Where(types => types.Definitions.TryGetValue(TypePath, out var d) && d is not null && state(d))
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence).Emit(because, ct);

    [Fact(Timeout = 300_000)]
    public async Task AMarkedMemberOfALiveCompiledType_IsRedactedTypedAndUntyped()
    {
        var ct = TestContext.Current.CancellationToken;
        var compiled = await CompileAndRegister(ct);

        var json = $$"""{"$type":"{{ContentTypeName}}","diagnosis":"{{Diagnosis}}","ward":"{{Ward}}"}""";

        // Untyped: resolved through the mesh-wide registry by $type.
        var untyped = Mesh.RedactJson(json);
        Output.WriteLine(untyped);
        untyped.Should().NotContain(Diagnosis, "the compiled type's marked member is found through its $type");
        untyped.Should().Contain(Ward, "CONTROL — the type RESOLVED: an unresolvable one would fail closed and redact this too");

        // Typed: an instance of the collectible type, marked on its positional parameter.
        var instance = JsonSerializer.Deserialize(json, compiled, Mesh.JsonSerializerOptions);
        instance.Should().NotBeNull();
        var typed = Mesh.RenderForLog(instance);
        Output.WriteLine(typed);
        typed.Should().NotContain(Diagnosis, "reflection reaches the attribute across the collectible load context");
        typed.Should().Contain(Ward);
        JsonSerializer.Serialize(instance, compiled, Mesh.JsonSerializerOptions).Should().Contain(Diagnosis,
            "the hub's own options — persistence — keep the value");

        Mesh.SensitiveValues(instance).Should().Contain(Diagnosis, "and the egress scrub can name it");
    }
}
