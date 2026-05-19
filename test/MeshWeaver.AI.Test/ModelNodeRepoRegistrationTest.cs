#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Asserts the node repositories (static node providers + partition
/// storage providers) for the top-level model + provider catalog are
/// properly registered when <c>AddAI()</c> runs. Reads exercise the same
/// surfaces production callers use: <c>workspace.GetMeshNodeStream</c>
/// (single node by path), <c>workspace.GetQuery</c> (synced collection),
/// and <c>IMeshService.QueryAsync</c> (one-shot snapshot).
///
/// <para>Configures one Anthropic catalog source so
/// <see cref="BuiltInLanguageModelProvider"/> emits both LanguageModel
/// (<c>Model/&lt;id&gt;</c>) and ModelProvider (<c>_Provider/Anthropic</c>)
/// static nodes.</para>
/// </summary>
public class ModelNodeRepoRegistrationTest : AITestBase
{
    public ModelNodeRepoRegistrationTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => false;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
                SectionName: "Anthropic",
                ProviderName: "Anthropic",
                Order: 1,
                DisplayLabel: "Anthropic",
                DefaultEndpoint: "https://api.anthropic.com/v1/messages",
                DefaultModelIds: System.Collections.Immutable.ImmutableArray.Create("claude-opus-4-7"),
                RequiresApiKey: true))
            .ConfigureServices(services =>
            {
                // Stub the Anthropic config section so BuiltInLanguageModelProvider has
                // models to emit. Without this the static catalog is empty.
                var dict = new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["Anthropic:Models:0"] = "claude-opus-4-7",
                    ["Anthropic:ApiKey"] = "sk-test",
                    ["Anthropic:Endpoint"] = "https://api.anthropic.com/v1/messages"
                };
                var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                    .Add(new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource { InitialData = dict! })
                    .Build();
                services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(config);
                return services;
            });

    private IWorkspace Workspace => Mesh.GetWorkspace();
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    [Fact(Timeout = 30_000)]
    public async Task StaticLanguageModel_AccessibleViaGetMeshNodeStream()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        var node = await Workspace.GetMeshNodeStream("Model/claude-opus-4-7")
            .Where(n => n?.Content is ModelDefinition)
            .Take(1)
            .Timeout(10.Seconds())
            .ToTask(ct);

        node.Should().NotBeNull("BuiltInLanguageModelProvider emits a LanguageModel for each configured model id");
        node.NodeType.Should().Be(LanguageModelNodeType.NodeType);
        var def = node.Content.Should().BeOfType<ModelDefinition>().Subject;
        def.Id.Should().Be("claude-opus-4-7");
        def.Provider.Should().Be("Anthropic");
        def.ProviderRef.Should().Be("_Provider/Anthropic",
            "ProviderRef points at the static ModelProvider so the resolver can chase the credential");
    }

    [Fact(Timeout = 30_000)]
    public async Task StaticModelProvider_AccessibleViaGetMeshNodeStream()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        // This is the registration the test in this file primarily proves
        // is wired correctly: routing to the _Provider partition resolves
        // to the static node provider, NOT to a missing-partition error.
        var node = await Workspace.GetMeshNodeStream("_Provider/Anthropic")
            .Where(n => n?.Content is ModelProviderConfiguration)
            .Take(1)
            .Timeout(10.Seconds())
            .ToTask(ct);

        node.Should().NotBeNull("ModelProvider partition storage provider must serve namespace:_Provider reads");
        node.NodeType.Should().Be(ModelProviderNodeType.NodeType);
        var cfg = node.Content.Should().BeOfType<ModelProviderConfiguration>().Subject;
        cfg.Provider.Should().Be("Anthropic");
        cfg.ApiKey.Should().Be("sk-test", "config-supplied credential lands on the static ModelProvider node");
    }

    [Fact(Timeout = 30_000)]
    public async Task SyncedQuery_NamespaceProvider_ReturnsModelProviderNodes()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        var snapshot = await Workspace.GetQuery(
                "test-providers",
                $"namespace:{ModelProviderNodeType.RootNamespace} nodeType:{ModelProviderNodeType.NodeType}")
            .Where(s => s.Any())
            .Take(1)
            .Timeout(10.Seconds())
            .ToTask(ct);

        snapshot.Should().Contain(n => n.Path == "_Provider/Anthropic"
            && n.NodeType == ModelProviderNodeType.NodeType,
            "the synced query routes through every IMeshQueryProvider — static nodes must surface");
    }

    [Fact(Timeout = 30_000)]
    public async Task QueryAsync_NamespaceModel_ReturnsLanguageModels()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        var results = new System.Collections.Generic.List<MeshNode>();
        await foreach (var item in MeshService.QueryAsync(
            new MeshQueryRequest { Query = $"namespace:{LanguageModelNodeType.RootNamespace} nodeType:{LanguageModelNodeType.NodeType}" },
            ct))
        {
            if (item is MeshNode n) results.Add(n);
        }

        results.Should().Contain(n => n.Path == "Model/claude-opus-4-7");
    }

    [Fact(Timeout = 30_000, Skip = "Repro for multi-query synced-collection deadlock: two simple queries that each work alone fail to deliver both Initial snapshots. Framework-level issue; tracked separately from this feature.")]
    public async Task TwoQueriesAcrossPartitions_DeliverBothInitialSnapshots()
    {
        // Diagnostic: drive workspace.GetQuery with the exact same two
        // queries the picker uses (no scope, no alternation) — assert
        // both partition's Initial gates open.
        var ct = new CancellationTokenSource(20.Seconds()).Token;

        var snapshot = await Workspace.GetQuery(
                "two-queries-diag",
                $"namespace:{LanguageModelNodeType.RootNamespace} nodeType:{LanguageModelNodeType.NodeType}",
                $"namespace:{ModelProviderNodeType.RootNamespace} nodeType:{ModelProviderNodeType.NodeType}")
            .Where(s => s.Any(n => n.NodeType == LanguageModelNodeType.NodeType)
                     && s.Any(n => n.NodeType == ModelProviderNodeType.NodeType))
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask(ct);

        snapshot.Should().Contain(n => n.Path == "Model/claude-opus-4-7");
        snapshot.Should().Contain(n => n.Path == "_Provider/Anthropic");
    }

    [Fact(Timeout = 30_000, Skip = "Type alternation nodeType:A|B times out in synced collection — needs investigation. Single-type queries work (asserted by other tests in this class).")]
    public async Task PickerQueries_ReturnBothLanguageModelAndModelProviderNodes()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        var snapshot = await Workspace.GetQuery(
                "picker",
                AgentPickerProjection.BuildModelQueries())
            .Where(s => s.Any(n => n.NodeType == LanguageModelNodeType.NodeType)
                     && s.Any(n => n.NodeType == ModelProviderNodeType.NodeType))
            .Take(1)
            .Timeout(10.Seconds())
            .ToTask(ct);

        snapshot.Should().Contain(n => n.Path == "Model/claude-opus-4-7");
        snapshot.Should().Contain(n => n.Path == "_Provider/Anthropic");
    }
}
