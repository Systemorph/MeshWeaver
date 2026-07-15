#pragma warning disable CS1591

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
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
/// <c>{userId}/_Memex/{provider}</c> + N child LanguageModel nodes, that
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
            // A ProviderModelLister backed by a stub Ollama /api/show handler: CreateProvider probes
            // tool support for OpenAICompatible models (only) and stamps ModelDefinition.SupportsTools.
            // Anthropic (used by the other tests) is never probed, so the stub is inert for them.
            .ConfigureServices(services => services
                .AddSingleton(sp => new ProviderModelLister(
                    sp.GetRequiredService<IMessageHub>(), logger: null, httpClient: new HttpClient(new StubShowHandler())))
                .AddSingleton<ModelProviderService>());

    /// <summary>
    /// Stubs Ollama's <c>/api/show</c> capability probe: a roleplay model (id contains "mythalion")
    /// reports <c>[completion]</c> (tool-less); everything else reports <c>[completion, tools]</c>.
    /// </summary>
    private sealed class StubShowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var toolLess = body.Contains("mythalion", StringComparison.OrdinalIgnoreCase);
            var json = toolLess
                ? "{\"capabilities\":[\"completion\"]}"
                : "{\"capabilities\":[\"completion\",\"tools\"]}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private ModelProviderService Service => Mesh.ServiceProvider.GetRequiredService<ModelProviderService>();
    private IWorkspace Workspace => Mesh.GetWorkspace();

    [Fact]
    public async Task CreateProvider_CreatesProviderNode_AndDefaultModelChildren()
    {
        var owner = $"user-{Guid.NewGuid():N}";

        var result = await Service.CreateProvider(
            ownerPath: owner,
            provider: "Anthropic",
            apiKey: "sk-ant-TEST-1234",
            label: "Roland's test key")
            .Should().Within(20.Seconds()).Emit();

        result.ProviderNode.Path.Should().Be($"{ModelProviderNodeType.UserNamespacePath(owner)}/Anthropic");
        result.ProviderNode.NodeType.Should().Be(ModelProviderNodeType.NodeType);

        var cfg = result.ProviderNode.Content.Should().BeOfType<ModelProviderConfiguration>().Which;
        cfg.Provider.Should().Be("Anthropic");
        cfg.ApiKey.Should().Be("sk-ant-TEST-1234");
        cfg.Label.Should().Be("Roland's test key");

        result.ModelNodes.Should().NotBeEmpty();
        result.ModelNodes.Should().AllSatisfy(node =>
        {
            node.NodeType.Should().Be(LanguageModelNodeType.NodeType);
            node.Path.Should().StartWith($"{ModelProviderNodeType.UserNamespacePath(owner)}/Anthropic/");
            var def = node.Content.Should().BeOfType<ModelDefinition>().Which;
            def.Provider.Should().Be("Anthropic");
            def.ProviderRef.Should().Be($"{ModelProviderNodeType.UserNamespacePath(owner)}/Anthropic");
        });
    }

    [Fact]
    public async Task CreateProvider_OpenAICompatible_StampsProbedToolSupportPerModel()
    {
        var owner = $"user-{Guid.NewGuid():N}";

        var result = await Service.CreateProvider(
                ownerPath: owner,
                provider: "OpenAICompatible",
                apiKey: "ollama",
                endpointOverride: "http://ollama:11434/v1",
                modelIdsOverride: new[] { "qwen3.6-code", "hf.co/TheBloke/Mythalion-13B-GGUF:Q4_K_M" })
            .Should().Within(20.Seconds()).Emit();

        result.ModelNodes.Should().HaveCount(2);
        var byId = result.ModelNodes.ToDictionary(
            n => ((ModelDefinition)n.Content!).Id, n => (ModelDefinition)n.Content!);

        byId["qwen3.6-code"].SupportsTools.Should().BeTrue(
            "Ollama /api/show reports the 'tools' capability for this model");
        byId["hf.co/TheBloke/Mythalion-13B-GGUF:Q4_K_M"].SupportsTools.Should().BeFalse(
            "a roleplay model reporting only [completion] must be stamped tool-less so the round omits tools");
    }

    [Fact]
    public async Task CreateProvider_NonOllamaProvider_LeavesToolSupportUnprobed()
    {
        var owner = $"user-{Guid.NewGuid():N}";

        // Anthropic is not an Ollama endpoint — CreateProvider must NOT probe /api/show (it would 404),
        // so SupportsTools stays null (unknown → assume supported), the historical behaviour.
        var result = await Service.CreateProvider(owner, "Anthropic", "sk-ant-x")
            .Should().Within(20.Seconds()).Emit();

        result.ModelNodes.Should().NotBeEmpty();
        result.ModelNodes.Should().AllSatisfy(n =>
            ((ModelDefinition)n.Content!).SupportsTools.Should().BeNull());
    }

    [Fact]
    public async Task GetProvidersForOwner_ReturnsLiveCollection()
    {
        var owner = $"user-{Guid.NewGuid():N}";

        var before = await Service.GetProvidersForOwner(owner)
            .Should().Within(5.Seconds()).Emit();
        before.Should().BeEmpty();

        await Service.CreateProvider(owner, "Anthropic", "sk-x")
            .Should().Within(20.Seconds()).Emit();

        var after = await Service.GetProvidersForOwner(owner)
            .Should().Within(10.Seconds()).Match(list => list.Count > 0);

        after.Should().HaveCount(1);
        after[0].Provider.Should().Be("Anthropic");
        // The DTO exposes a fingerprint, never the literal key.
        after[0].ApiKeyFingerprint.Should().NotBe("(empty)");
        after[0].ApiKeyFingerprint.Length.Should().Be(8);
    }

    [Fact]
    public async Task RotateKey_UpdatesApiKeyOnly()
    {
        var owner = $"user-{Guid.NewGuid():N}";

        var created = await Service.CreateProvider(owner, "Anthropic", "sk-old", label: "L")
            .Should().Within(20.Seconds()).Emit();
        var path = created.ProviderNode.Path;

        // Pre-warm the synced query so the Update finds the path registered.
        await Service.GetProvidersForOwner(owner)
            .Should().Within(10.Seconds()).Match(p => p.Count > 0);

        var ok = await Service.RotateKey(path, "sk-new").Should().Emit();
        ok.Should().BeTrue();

        var updated = await Workspace.GetMeshNodeStream(path)
            .Should().Within(10.Seconds()).Match(n => (n.Content as ModelProviderConfiguration)?.ApiKey == "sk-new");
        var cfg = updated.Content.Should().BeOfType<ModelProviderConfiguration>().Which;
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

        // Stays on the live single-subscription wait (NOT a reactive .Should()):
        // GetProvidersForOwner is a Replay(1).RefCount() cached stream. The
        // cascade-delete empty snapshot arrives on the live upstream re-emission;
        // a blocking reactive .Should() raced the Replay/RefCount reconnect and
        // timed out, whereas this stays subscribed until the empty state lands.
        // No reactive blocking assertion here, so the method correctly stays async.
        var afterDelete = await Service.GetProvidersForOwner(owner)
            .Where(list => list.Count == 0).Take(1).Timeout(10.Seconds()).ToTask(ct);
        afterDelete.Should().BeEmpty();
    }
}
