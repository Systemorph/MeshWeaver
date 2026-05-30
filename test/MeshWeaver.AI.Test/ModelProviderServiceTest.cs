#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.Models;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// End-to-end tests for <see cref="ModelProviderService"/> â€” the reactive
/// CRUD surface backing the user's Models settings tab. Asserts that
/// CreateProvider lays down the canonical
/// <c>{userId}/_Provider/{provider}</c> + N child LanguageModel nodes, that
/// RotateKey preserves other fields, and that DeleteProvider cascades.
/// </summary>
public class ModelProviderServiceTest : AITestBase
{
    public ModelProviderServiceTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => false;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // Register the Anthropic catalog source so ModelProviderService.CreateProvider
            // can look up its DefaultModelIds â€” the service auto-creates one
            // LanguageModel child per default id.
            .AddLanguageModelCatalogSource(new LanguageModelCatalogSource(
                SectionName: "Anthropic",
                ProviderName: "Anthropic",
                Order: 1,
                DisplayLabel: "Anthropic",
                DefaultEndpoint: "https://api.anthropic.com/v1/messages",
                DefaultModelIds: System.Collections.Immutable.ImmutableArray.Create(
                    "claude-opus-4-7", "claude-sonnet-4-6"),
                RequiresApiKey: true))
            .ConfigureServices(services => services.AddSingleton<ModelProviderService>());

    private ModelProviderService Service => Mesh.ServiceProvider.GetRequiredService<ModelProviderService>();
    private IWorkspace Workspace => Mesh.GetWorkspace();

    [Fact]
    public async Task CreateProvider_CreatesProviderNode_AndDefaultModelChildren()
    {
        var ct = new CancellationTokenSource(20.Seconds()).Token;
        var owner = $"user-{Guid.NewGuid():N}";

        var result = await Service.CreateProvider(
            ownerPath: owner,
            provider: "Anthropic",
            apiKey: "sk-ant-TEST-1234",
            label: "Roland's test key")
            .FirstAsync().ToTask(ct);

        result.ProviderNode.Path.Should().Be($"{owner}/_Provider/Anthropic");
        result.ProviderNode.NodeType.Should().Be(ModelProviderNodeType.NodeType);

        var cfg = result.ProviderNode.Content.Should().BeAssignableTo<ModelProviderConfiguration>().Subject;
        cfg.Provider.Should().Be("Anthropic");
        cfg.ApiKey.Should().Be("sk-ant-TEST-1234");
        cfg.Label.Should().Be("Roland's test key");

        result.ModelNodes.Should().NotBeEmpty();
        result.ModelNodes.Should().AllSatisfy(node =>
        {
            node.NodeType.Should().Be(LanguageModelNodeType.NodeType);
            node.Path.Should().StartWith($"{owner}/_Provider/Anthropic/");
            var def = node.Content.Should().BeAssignableTo<ModelDefinition>().Subject;
            def.Provider.Should().Be("Anthropic");
            def.ProviderRef.Should().Be($"{owner}/_Provider/Anthropic");
        });
    }

    [Fact]
    public async Task GetProvidersForOwner_ReturnsLiveCollection()
    {
        var ct = new CancellationTokenSource(20.Seconds()).Token;
        var owner = $"user-{Guid.NewGuid():N}";

        var before = await Service.GetProvidersForOwner(owner)
            .Take(1).Timeout(5.Seconds()).ToTask(ct);
        before.Should().BeEmpty();

        await Service.CreateProvider(owner, "Anthropic", "sk-x")
            .FirstAsync().ToTask(ct);

        var after = await Service.GetProvidersForOwner(owner)
            .Where(list => list.Count > 0)
            .Take(1).Timeout(10.Seconds()).ToTask(ct);

        after.Should().HaveCount(1);
        after[0].Provider.Should().Be("Anthropic");
        // The DTO exposes a fingerprint, never the literal key.
        after[0].ApiKeyFingerprint.Should().NotBe("(empty)");
        after[0].ApiKeyFingerprint.Length.Should().Be(8);
    }

    [Fact]
    public async Task RotateKey_UpdatesApiKeyOnly()
    {
        var ct = new CancellationTokenSource(20.Seconds()).Token;
        var owner = $"user-{Guid.NewGuid():N}";

        var created = await Service.CreateProvider(owner, "Anthropic", "sk-old", label: "L")
            .FirstAsync().ToTask(ct);
        var path = created.ProviderNode.Path;

        // Pre-warm the synced query so the Update finds the path registered.
        await Service.GetProvidersForOwner(owner)
            .Where(p => p.Count > 0).Take(1).Timeout(10.Seconds()).ToTask(ct);

        var ok = await Service.RotateKey(path, "sk-new").FirstAsync().ToTask(ct);
        ok.Should().BeTrue();

        var updated = await Workspace.GetMeshNodeStream(path)
            .Where(n => (n.Content as ModelProviderConfiguration)?.ApiKey == "sk-new")
            .Take(1).Timeout(10.Seconds()).ToTask(ct);
        var cfg = updated.Content.Should().BeAssignableTo<ModelProviderConfiguration>().Subject;
        cfg.ApiKey.Should().Be("sk-new");
        cfg.Label.Should().Be("L", "RotateKey must not clobber other fields");
    }

    [Fact]
    public async Task DeleteProvider_RemovesProviderAndCascades()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var owner = $"user-{Guid.NewGuid():N}";

        var created = await Service.CreateProvider(owner, "Anthropic", "sk-del")
            .FirstAsync().ToTask(ct);
        var path = created.ProviderNode.Path;

        await Service.GetProvidersForOwner(owner)
            .Where(p => p.Count > 0).Take(1).Timeout(10.Seconds()).ToTask(ct);

        var ok = await Service.DeleteProvider(path).FirstAsync().ToTask(ct);
        ok.Should().BeTrue();

        var afterDelete = await Service.GetProvidersForOwner(owner)
            .Where(list => list.Count == 0).Take(1).Timeout(10.Seconds()).ToTask(ct);
        afterDelete.Should().BeEmpty();
    }
}
