using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// The <b>Provider</b>-catalog analog of <see cref="NodeTypeCatalogTest"/> (the Harness catalog),
/// pinning the unified NodeType-catalog contract for the AI model catalog (see
/// <c>Doc/Architecture/NodeTypeCatalogs.md</c>).
///
/// <para>The model catalog ships INSTANCES of two NodeTypes — <c>ModelProvider</c> (providers +
/// credentials) and <c>LanguageModel</c> (the models) — under the top-level <c>Provider</c> partition,
/// materialized into the DB by <see cref="ModelStaticRepoSource"/> when the partition is DB-synced.
/// The bug this pins (the same class HarnessNodeType fixed): on the DB-synced path the in-memory
/// <c>@ModelProvider</c> / <c>@LanguageModel</c> type-def hubs would activate and the per-node-hub
/// persistence sampler would auto-persist their in-memory own node, which the PG router routes by
/// first path segment to a never-provisioned <c>modelprovider</c> / <c>languagemodel</c> schema →
/// <c>42P01</c>. <see cref="MeshNode.IsDefinitionOnly"/> dissociates the type-defs from runtime
/// serving so the sampler never writes them — the def still supplies the HubConfiguration delegate by
/// name + proves the type exists.</para>
/// </summary>
public class ProviderCatalogTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // DB-synced Provider partition (as the distributed portal wires it): the in-memory static surfaces
    // are gated off, and the catalog is materialized into persistence by ModelStaticRepoSource. One
    // Anthropic catalog source + a stub config section give BuiltInLanguageModelProvider real
    // providers/models to emit.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .AddAI(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ModelProviderNodeType.RootNamespace })
            .AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
                SectionName: "Anthropic",
                ProviderName: "Anthropic",
                Order: 1,
                DisplayLabel: "Anthropic",
                DefaultEndpoint: "https://api.anthropic.com/v1/messages",
                DefaultModelIds: ImmutableArray.Create("claude-opus-4-7"),
                RequiresApiKey: true))
            .ConfigureServices(services =>
            {
                var dict = new Dictionary<string, string?>
                {
                    ["Anthropic:Models:0"] = "claude-opus-4-7",
                    ["Anthropic:ApiKey"] = "sk-test",
                    ["Anthropic:Endpoint"] = "https://api.anthropic.com/v1/messages",
                };
                var config = new ConfigurationBuilder()
                    .Add(new MemoryConfigurationSource { InitialData = dict! })
                    .Build();
                services.AddSingleton<IConfiguration>(config);
                return services;
            })
            .ConfigureServices(s => s.AddSingleton<IStaticRepoSource>(sp =>
                new ModelStaticRepoSource(sp.GetRequiredService<BuiltInLanguageModelProvider>())));

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ProviderPath = "Provider/Anthropic";
    private const string ModelPath = "Provider/Anthropic/claude-opus-4-7";

    /// <summary>Materialize the Provider catalog into persistence (the DB-synced/import path).</summary>
    private async Task ImportAsync(CancellationToken ct)
    {
        var results = await StaticRepoImporter.ImportAll(Mesh).ToList().FirstAsync().ToTask(ct);
        foreach (var r in results)
            Output.WriteLine($"import: partition={r.Partition} outcome={r.Outcome} count={r.Count}");
    }

    /// <summary>Run a query, retrying until <paramref name="predicate"/> holds, then return the items.</summary>
    private Task<IReadOnlyList<MeshNode>> QueryUntil(
        string query, Func<IReadOnlyList<MeshNode>, bool> predicate, CancellationToken ct)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        return Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
                .Where(c => c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                .Take(1)
                .Select(c => (IReadOnlyList<MeshNode>)c.Items))
            .Where(predicate)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync()
            .ToTask(ct);
    }

    /// <summary>One-shot Initial snapshot for a query — used to assert ABSENCE once the system is warm.</summary>
    private Task<IReadOnlyList<MeshNode>> QueryOnce(string query, CancellationToken ct)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        return meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Where(c => c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
            .Take(1)
            .Select(c => (IReadOnlyList<MeshNode>)c.Items)
            .Timeout(TimeSpan.FromSeconds(15))
            .FirstAsync()
            .ToTask(ct);
    }

    /// <summary>
    /// (a) The in-memory <c>ModelProvider</c> + <c>LanguageModel</c> type-defs are DEFINITION-ONLY
    /// on the DB-synced path, yet still resolve via <c>FindStaticNode</c> (so HubConfiguration-by-name
    /// + type-existence still work). This is what stops the per-node-hub persistence sampler from
    /// auto-writing them to a phantom <c>modelprovider</c>/<c>languagemodel</c> schema (42P01).
    /// </summary>
    [Fact(Timeout = 90000)]
    public void TypeDefs_AreDefinitionOnly_ButStillResolveViaFindStaticNode()
    {
        var providerDef = Mesh.ServiceProvider.FindStaticNode(ModelProviderNodeType.NodeType);
        providerDef.Should().NotBeNull("the static 'ModelProvider' definition must remain for HubConfiguration resolution");
        providerDef!.IsDefinitionOnly.Should().BeTrue(
            "a DB-synced catalog's in-memory type-def is dissociated from runtime node-serving");
        providerDef.HubConfiguration.Should().NotBeNull(
            "the definition-only node still supplies the (non-serialisable) HubConfiguration delegate by name");

        var modelDef = Mesh.ServiceProvider.FindStaticNode(LanguageModelNodeType.NodeType);
        modelDef.Should().NotBeNull("the static 'LanguageModel' definition must remain for HubConfiguration resolution");
        modelDef!.IsDefinitionOnly.Should().BeTrue();
        modelDef.HubConfiguration.Should().NotBeNull();

        var selectionDef = Mesh.ServiceProvider.FindStaticNode(ModelProviderNodeType.SelectionNodeType);
        selectionDef.Should().NotBeNull("the static 'ModelProviderSelection' definition must remain");
        selectionDef!.IsDefinitionOnly.Should().BeTrue();
    }

    /// <summary>
    /// (b)+(c) After import the catalog INSTANCES serve from the <c>Provider</c> partition, and NO node
    /// is served at the bare discriminator paths <c>@ModelProvider</c> / <c>@LanguageModel</c> — the
    /// definition-only type-defs are excluded from query results, so nothing was ever persisted there
    /// (the 42P01 write the dissociation prevents).
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task Instances_ServeFromProviderPartition_AndNothingServesAtDiscriminatorPath()
    {
        var ct = Ct;
        await ImportAsync(ct);

        // (c) the model picker query returns the materialized catalog instances (served from the DB partition).
        var typeFilter = $"{ModelProviderNodeType.NodeType}|{LanguageModelNodeType.NodeType}";
        var instances = await QueryUntil(
            $"namespace:{ModelProviderNodeType.RootNamespace} nodeType:{typeFilter} scope:descendants",
            list => list.Any(n => string.Equals(n.Path, ProviderPath, StringComparison.Ordinal)), ct);

        instances.Should().Contain(n => n.Path == ProviderPath && n.NodeType == ModelProviderNodeType.NodeType,
            "namespace:Provider must return the materialized ModelProvider instance");
        instances.Should().Contain(n => n.Path == ModelPath && n.NodeType == LanguageModelNodeType.NodeType,
            "namespace:Provider must return the materialized LanguageModel instance");

        // (b) the system is warm (instances above resolved); the bare discriminator paths must serve
        // NOTHING — the type-defs are definition-only (excluded from queries) and the persistence
        // sampler never auto-wrote them to a phantom modelprovider/languagemodel schema.
        var atModelProvider = await QueryOnce($"path:{ModelProviderNodeType.NodeType}", ct);
        atModelProvider.Should().NotContain(n => string.Equals(n.Path, ModelProviderNodeType.NodeType, StringComparison.Ordinal),
            "no runtime node is served/persisted at the bare @ModelProvider discriminator path");

        var atLanguageModel = await QueryOnce($"path:{LanguageModelNodeType.NodeType}", ct);
        atLanguageModel.Should().NotContain(n => string.Equals(n.Path, LanguageModelNodeType.NodeType, StringComparison.Ordinal),
            "no runtime node is served/persisted at the bare @LanguageModel discriminator path");
    }

    /// <summary>
    /// The <c>Provider</c> partition root resolves to the single <c>nodeType:Space</c> landing node the
    /// import materializes — there is no second claimant at <c>@Provider</c> (the type-defs live at the
    /// distinct <c>@ModelProvider</c>/<c>@LanguageModel</c> discriminator paths, so unlike Harness/Agent
    /// there is no possibility of a partition-root collision here).
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task ProviderPartitionRoot_IsSingleSpaceNode()
    {
        var ct = Ct;
        await ImportAsync(ct);

        var items = await QueryUntil($"path:{ModelProviderNodeType.RootNamespace}",
            list => list.Any(n => string.Equals(n.Path, ModelProviderNodeType.RootNamespace, StringComparison.Ordinal)),
            ct);
        var atPath = items
            .Where(n => string.Equals(n.Path, ModelProviderNodeType.RootNamespace, StringComparison.Ordinal))
            .ToList();
        atPath.Should().HaveCount(1, "the Provider partition path must have exactly one claimant — the import's Space root");
        atPath[0].NodeType.Should().Be("Space", "the Provider catalog root is a nodeType:Space landing page");
    }
}
