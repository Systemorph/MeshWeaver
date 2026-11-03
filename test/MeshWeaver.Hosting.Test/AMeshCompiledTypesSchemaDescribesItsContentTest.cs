using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A NodeType's schema describes the CONTENT its instances carry — with that content's XML-doc
/// descriptions — for a type compiled IN the mesh, with no instance ever activated.</b>
///
/// <para><b>What it was</b> (measured on a client instance, 2026-11-03, through its MCP server). The data
/// model explorer's JSON Schema / UML / Table tabs compose the type node's own <c>$Schema</c> and
/// <c>$Model</c> areas, and showed nothing of the content type:</para>
/// <list type="bullet">
///   <item><c>@Claims/Claim/area/$Schema</c> rendered <c>MeshNode</c> and <c>Content Type: NodeTypeDefinition</c>
///     — the definition hub's own shape (the generic node-schema area reads the hub's own
///     <c>WithContentType</c>, which on a NodeType hub IS <see cref="NodeTypeDefinition"/>).</item>
///   <item><c>@Claims/Claim/schema/</c> answered <c>NodeTypeDefinition</c>; <c>@Claims/Claim/schema/Claim</c>
///     answered <c>{}</c>. The unified <c>schema/…</c> path reduces a workspace <see cref="SchemaReference"/>
///     over the definition hub's registry; the one interceptor meant to redirect it matched only a bare,
///     untyped <see cref="SchemaReference"/> request — never the path MCP and the GUI send — and returned
///     the delivery unprocessed, so the default answered first anyway.</item>
///   <item>Where an instance's schema DID describe the content (<c>@{policy}/schema/</c> of a
///     <c>Reinsurance/Acceptance</c>), no property carried a <c>description</c>: the dll a hub loads (the
///     assembly store's content-hashed copy, or an adopted bundle) never had the compiler's
///     <c>{AssemblyName}.xml</c> beside it. Covered at the reader in
///     <c>MeshCompiledTypesCarryTheirDocumentationTest</c>; asserted end to end here.</item>
/// </list>
///
/// <para>The type is compiled by the real batch bake (Roslyn, the assembly store, the record's build
/// stamp) and NO hub of it is activated — the state of a replica that adopted or baked a type nobody
/// opened, which is every type the explorer lists.</para>
/// </summary>
public class AMeshCompiledTypesSchemaDescribesItsContentTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan PerTypeBudget = TimeSpan.FromMinutes(3);

    private readonly string suffix = Guid.NewGuid().ToString("N")[..8];

    // The type's own name differs from its content type's — as Esl/…/DirCarrier → EslOfficeCarrier does.
    private string TypeId => $"Carrier{suffix}";
    private string TypePath => $"{TestPartition}/{TypeId}";
    private string ContentTypeName => $"CarrierContent{suffix}";

    private const string TypeSummary = "A carrier the office places risks with.";
    private const string PropertySummary = "The legal name of the carrier, as registered.";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private async Task SeedAndBake(CancellationToken ct)
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
                             Code = $$"""
                                 /// <summary>{{TypeSummary}}</summary>
                                 public record {{ContentTypeName}}
                                 {
                                     /// <summary>{{PropertySummary}}</summary>
                                     public string? LegalName { get; init; }
                                 }
                                 """,
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
        await Enumerate(d => !string.IsNullOrEmpty(d.LatestAssemblyPath) && d.LastCompiledVersion is not null,
            "the bake's build stamp lands on the NodeType record", ct);
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

    private async Task<SchemaInfo> Schema(string unifiedPath, CancellationToken ct)
    {
        var client = GetClient();
        var response = await client
            .Observe(new GetDataRequest(new UnifiedReference(unifiedPath)), o => o.WithTarget(new Address(TypePath)))
            .Should().Within(PerTypeBudget).Emit($"'{TypePath}/{unifiedPath}' is answered", ct);
        response.Message.Error.Should().BeNull();
        var info = response.Message.Data.As<SchemaInfo>(client.JsonSerializerOptions);
        info.Should().NotBeNull();
        Output.WriteLine("{0} → {1}: {2}", unifiedPath, info!.Type, info.Schema);
        return info;
    }

    private IObservable<string> Render(LayoutAreaReference reference)
        => GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(TypePath), reference)
            .Select(change => change.Value.GetRawText());

    [Fact(Timeout = 300_000)]
    public async Task TheTypesSchemaPath_DescribesItsContentType_WithItsDocumentation()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAndBake(ct);

        var untyped = await Schema("schema/", ct);
        untyped.Type.Should().Be(ContentTypeName,
            "@{type}/schema/ answers the content type the configuration declares — never NodeTypeDefinition");
        untyped.Schema.Should().Contain("legalName");
        untyped.Schema.Should().Contain(PropertySummary,
            "the property's /// <summary> is its description — the mesh-compiled image carries its docs");
        untyped.Schema.Should().Contain(TypeSummary, "and so is the type's");

        (await Schema($"schema/{ContentTypeName}", ct)).Schema.Should().Contain(PropertySummary,
            "schema/{T} names a type of the instance model — it answered '{}' before");
        (await Schema($"schema/{TypeId}", ct)).Type.Should().Be(ContentTypeName,
            "the NodeType's own name addresses its content type");
        (await Schema($"schema/{nameof(NodeTypeDefinition)}", ct)).Schema.Should().Contain("configuration",
            "a type only the definition hub knows still answers from its own registry");
    }

    [Fact(Timeout = 300_000)]
    public async Task TheSchemaAndModelAreas_ShowTheContentType_AsTheExplorersTabsComposeThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAndBake(ct);

        // JSON Schema tab: $Schema on the type node.
        var schema = await Render(new LayoutAreaReference(MeshNodeLayoutAreas.SchemaArea))
            .Should().Within(PerTypeBudget)
            .Match(json => json.Contains($"Content Type: {ContentTypeName}"),
                "the $Schema area on a NodeType names its instances' content type", ct);
        schema.Should().Contain(PropertySummary, "with the property descriptions");
        schema.Should().NotContain($"Content Type: {nameof(NodeTypeDefinition)}",
            "never the definition hub's own shape — what the client instance showed");

        // Table tab: $Model with the type node's leaf name as the Id.
        await Render(new LayoutAreaReference(MeshNodeLayoutAreas.ModelArea) { Id = TypeId })
            .Should().Within(PerTypeBudget)
            .Match(json => json.Contains(ContentTypeName) && json.Contains("LegalName")
                    && json.Contains(PropertySummary) && !json.Contains("Type not found"),
                "$Model/{own name} renders the content type's property table with descriptions", ct);

        // UML tab: $Model with no Id — the class diagram.
        await Render(new LayoutAreaReference(MeshNodeLayoutAreas.ModelArea))
            .Should().Within(PerTypeBudget)
            .Match(json => json.Contains("classDiagram") && json.Contains(ContentTypeName),
                "the UML of a NodeType draws its content type", ct);
    }
}
